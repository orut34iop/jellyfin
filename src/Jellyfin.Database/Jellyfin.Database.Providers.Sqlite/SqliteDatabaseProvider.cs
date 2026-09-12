using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Configures jellyfin to use an SQLite database.
/// </summary>
[JellyfinDatabaseProviderKey("Jellyfin-SQLite")]
public sealed class SqliteDatabaseProvider : IJellyfinDatabaseProvider
{
    private const string BackupFolderName = "SQLiteBackups";
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<SqliteDatabaseProvider> _logger;
    private readonly SqliteCorruptionGuard _corruptionGuard;
    private string? _databasePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabaseProvider"/> class.
    /// </summary>
    /// <param name="applicationPaths">Service to construct the fallback when the old data path configuration is used.</param>
    /// <param name="logger">A logger.</param>
    /// <param name="applicationLifetime">The running host, when available.</param>
    public SqliteDatabaseProvider(IApplicationPaths applicationPaths, ILogger<SqliteDatabaseProvider> logger, IHostApplicationLifetime? applicationLifetime = null)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _corruptionGuard = new SqliteCorruptionGuard(logger, applicationLifetime);
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public bool RequiresRecovery => _corruptionGuard.IsCorrupted;

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        _corruptionGuard.Initialize(Path.Combine(_applicationPaths.DataPath, "database-corruption.txt"));

        static T? GetOption<T>(ICollection<CustomDatabaseOption>? options, string key, Func<string, T> converter, Func<T>? defaultValue = null)
        {
            if (options is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            var value = options.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (value is null)
            {
                return defaultValue is not null ? defaultValue() : default;
            }

            return converter(value.Value);
        }

        var customOptions = databaseConfiguration.CustomProviderOptions?.Options;

        var sqliteConnectionBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = GetOption(customOptions, "path", e => e, () => Path.Combine(_applicationPaths.DataPath, "jellyfin.db")),
            // Private, not Default: sqlite3_enable_shared_cache is process-global, so a plugin
            // enabling it makes these connections share a cache too. Contention then surfaces as
            // SQLITE_LOCKED ("database table is locked"), which the busy handler does not cover,
            // so busy_timeout is skipped and the command fails at CommandTimeout instead.
            Cache = GetOption(customOptions, "cache", Enum.Parse<SqliteCacheMode>, () => SqliteCacheMode.Private),
            Pooling = GetOption(customOptions, "pooling", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => true),
            DefaultTimeout = GetOption(customOptions, "command-timeout", int.Parse, () => 60)
        };

        var connectionString = sqliteConnectionBuilder.ToString();
        _databasePath = sqliteConnectionBuilder.DataSource;

        // Log SQLite connection parameters
        _logger.LogInformation("SQLite connection string: {ConnectionString}", connectionString);

        options
            .UseSqlite(
                connectionString,
                sqLiteOptions => sqLiteOptions.MigrationsAssembly(GetType().Assembly))
            // TODO: Remove when https://github.com/dotnet/efcore/pull/35873 is merged & released
            .ConfigureWarnings(warnings =>
                warnings.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning)
                    .Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .AddInterceptors(_corruptionGuard, new PragmaConnectionInterceptor(
                _logger,
                GetOption<int?>(customOptions, "cacheSize", e => int.Parse(e, CultureInfo.InvariantCulture)),
                GetOption(customOptions, "lockingmode", e => e, () => "NORMAL")!,
                GetOption(customOptions, "journalsizelimit", int.Parse, () => 134_217_728),
                GetOption(customOptions, "tempstoremode", int.Parse, () => 2),
                GetOption(customOptions, "syncmode", int.Parse, () => 1),
                customOptions?.Where(e => e.Key.StartsWith("#PRAGMA:", StringComparison.OrdinalIgnoreCase)).ToDictionary(e => e.Key["#PRAGMA:".Length..], e => e.Value) ?? [],
                _corruptionGuard));

        var enableSensitiveDataLogging = GetOption(customOptions, "EnableSensitiveDataLogging", e => e.Equals(bool.TrueString, StringComparison.OrdinalIgnoreCase), () => false);
        if (enableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging(enableSensitiveDataLogging);
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on SQLite connection");
        }
    }

    /// <inheritdoc/>
    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        return OptimizeAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void OnDatabaseError(Exception exception) => _corruptionGuard.ObserveFailure(exception);

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.SetDefaultDateTimeKind(DateTimeKind.Utc);
    }

    /// <inheritdoc/>
    public async Task RunShutdownTask(CancellationToken cancellationToken)
    {
        if (_corruptionGuard.IsCorrupted)
        {
            _logger.LogWarning("Skipping database shutdown maintenance after corruption was reported.");
            SqliteConnection.ClearAllPools();
            return;
        }

        // Run before disposing the application
        try
        {
            await OptimizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A missed optimization only costs performance, so never fail the shutdown over this.
            _logger.LogError(ex, "Error while optimizing jellyfin.db");
        }

        SqliteConnection.ClearAllPools();
    }

    private async Task OptimizeAsync(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("VACUUM", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA analysis_limit=0", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("jellyfin.db optimized successfully!");
        }
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Conventions.Add(_ => new DoNotUseReturningClauseConvention());
    }

    /// <inheritdoc />
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _corruptionGuard.ThrowIfCorrupted();
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
        var path = _databasePath ?? Path.Combine(_applicationPaths.DataPath, "jellyfin.db");
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName);
        Directory.CreateDirectory(backupFile);

        backupFile = Path.Combine(backupFile, $"{key}_jellyfin.db");
        // Copy the logical database, including committed WAL frames. File.Copy only
        // captures the main file and can silently omit recent transactions.
        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        return key;
    }

    /// <inheritdoc />
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Structural corruption needs manual recovery; never overwrite the evidence automatically.
        _corruptionGuard.ThrowIfCorrupted();
        SqliteConnection.ClearAllPools();
        var path = _databasePath ?? Path.Combine(_applicationPaths.DataPath, "jellyfin.db");
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_jellyfin.db");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
            throw new FileNotFoundException("The database backup does not exist.", backupFile);
        }

        using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupFile,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        using (var check = source.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            using (var results = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await results.ReadAsync(cancellationToken).ConfigureAwait(false)
                    || results.GetString(0) != "ok"
                    || await results.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("Database backup failed integrity_check; restore refused.");
                }
            }

            check.CommandText = "PRAGMA foreign_key_check";
            using var foreignKeys = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await foreignKeys.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Database backup failed foreign_key_check; restore refused.");
            }
        }

        // Retain the pre-rollback state, even when migration rollback succeeds.
        var retainedKey = await MigrationBackupFast(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("Retained database before migration rollback as {Key}", retainedKey);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        // SQLite coordinates the destination transaction and its WAL. Never overwrite
        // a live main file while stale journal pages or other connections may remain.
        source.BackupDatabase(destination);
    }

    /// <inheritdoc />
    public Task DeleteBackup(string key)
    {
        var backupFile = Path.Combine(_applicationPaths.DataPath, BackupFolderName, $"{key}_jellyfin.db");

        if (!File.Exists(backupFile))
        {
            _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
            return Task.CompletedTask;
        }

        File.Delete(backupFile);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(tableNames);

        var deleteQueries = new List<string>();
        foreach (var tableName in tableNames)
        {
            deleteQueries.Add($"DELETE FROM \"{tableName}\";");
        }

        var deleteAllQuery =
        $"""
        PRAGMA foreign_keys = OFF;
        {string.Join('\n', deleteQueries)}
        PRAGMA foreign_keys = ON;
        """;

        await dbContext.Database.ExecuteSqlRawAsync(deleteAllQuery).ConfigureAwait(false);
    }
}
