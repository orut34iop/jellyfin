using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

public sealed class SqliteRecoverySafetyTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("jellyfin-recovery-test-").FullName;

    [Theory]
    [InlineData(11)]
    [InlineData(26)]
    public async Task StructuralFailure_StopsOnceAndSkipsShutdownMaintenance(int code)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var provider = CreateProvider(lifetime.Object);
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
        provider.DbContextFactory = factory.Object;
        provider.OnDatabaseError(new DbUpdateException("write failed", new SqliteException("corrupt", code)));
        provider.OnDatabaseError(new SqliteException("corrupt again", code));

        await provider.RunShutdownTask(TestContext.Current.CancellationToken).ConfigureAwait(true);

        lifetime.Verify(x => x.StopApplication(), Times.Once);
        factory.VerifyNoOtherCalls();
        Assert.True(provider.RequiresRecovery);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.MigrationBackupFast(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RestoreBackupFast("missing", TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(19)]
    public void RecoverableSqliteFailure_DoesNotLatch(int code)
    {
        var lifetime = new Mock<IHostApplicationLifetime>();
        var guard = new SqliteCorruptionGuard(NullLogger.Instance, lifetime.Object);
        guard.ObserveFailure(new SqliteException("ordinary error", code));
        guard.ThrowIfCorrupted();
        Assert.False(guard.IsCorrupted);
        lifetime.Verify(x => x.StopApplication(), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidDatabase_StopsHostAndBlocksSubsequentCommands(bool async)
    {
        var path = Path.Combine(_directory, "jellyfin.db");
        await File.WriteAllTextAsync(path, new string('x', 4096), TestContext.Current.CancellationToken).ConfigureAwait(true);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var provider = CreateProvider(lifetime.Object);
        var options = new DbContextOptionsBuilder();
        provider.Initialise(options, new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" });
        await using var context = new DbContext(options.Options);
        if (async)
        {
            await Assert.ThrowsAsync<SqliteException>(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE NeverWritten (Id INTEGER)", TestContext.Current.CancellationToken)).ConfigureAwait(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE NeverWritten (Id INTEGER)", TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }
        else
        {
            Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw("CREATE TABLE NeverWritten (Id INTEGER)"));
            Assert.Throws<InvalidOperationException>(() => context.Database.ExecuteSqlRaw("CREATE TABLE NeverWritten (Id INTEGER)"));
        }

        lifetime.Verify(x => x.StopApplication(), Times.Once);
        Assert.Equal(new string('x', 4096), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(File.Exists(Path.Combine(_directory, "database-corruption.txt")));
        Assert.Throws<InvalidOperationException>(() => CreateProvider().Initialise(new DbContextOptionsBuilder(), new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-SQLite" }));
    }

    [Fact]
    public async Task CommandFailure_WithoutConnectionPragmas_BlocksLaterReadsAndWrites()
    {
        var path = Path.Combine(_directory, "broken.db");
        await File.WriteAllTextAsync(path, new string('x', 4096), TestContext.Current.CancellationToken).ConfigureAwait(true);
        var lifetime = new Mock<IHostApplicationLifetime>();
        var guard = new SqliteCorruptionGuard(NullLogger.Instance, lifetime.Object);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        var options = new DbContextOptionsBuilder().UseSqlite(connection).AddInterceptors(guard).Options;
        await using var context = new DbContext(options);
        Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw("CREATE TABLE NeverWritten (Id INTEGER)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArrayAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Throws<InvalidOperationException>(() => context.Database.SqlQueryRaw<int>("SELECT 1 AS Value").ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE NeverWritten (Id INTEGER)", TestContext.Current.CancellationToken)).ConfigureAwait(true);
        lifetime.Verify(x => x.StopApplication(), Times.Once);
    }

    [Fact]
    public async Task BackupAndRestore_IncludeWalAndPreservePreRollbackStateAtCustomPath()
    {
        var path = Path.Combine(_directory, "custom.db");
        using var connection = Open(path);
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE Items (Id INTEGER PRIMARY KEY); INSERT INTO Items VALUES (1)");
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        var provider = CreateProvider();
        provider.Initialise(new DbContextOptionsBuilder(), new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = string.Empty,
                Options = [new CustomDatabaseOption { Key = "path", Value = path }]
            }
        });
        var key = await provider.MigrationBackupFast(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var backupPath = Path.Combine(_directory, "SQLiteBackups", key + "_jellyfin.db");
        using (var backup = Open(backupPath))
        {
            Assert.Equal(1L, Scalar(backup, "SELECT count(*) FROM Items"));
            Assert.Equal("ok", Scalar(backup, "PRAGMA integrity_check"));
        }

        Execute(connection, "INSERT INTO Items VALUES (2)");
        await provider.RestoreBackupFast(key, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM Items"));
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
        var files = Directory.GetFiles(Path.Combine(_directory, "SQLiteBackups"), "*_jellyfin.db");
        Assert.Equal(2, files.Length);
        var retainedPath = Assert.Single(files, file => file != backupPath);
        using var retained = Open(retainedPath);
        Assert.Equal(2L, Scalar(retained, "SELECT count(*) FROM Items"));
    }

    [Fact]
    public async Task Restore_RefusesForeignKeyViolationsWithoutChangingDestination()
    {
        using var current = Open(Path.Combine(_directory, "jellyfin.db"));
        Execute(current, "CREATE TABLE KeepMe (Id INTEGER); INSERT INTO KeepMe VALUES (7)");
        Directory.CreateDirectory(Path.Combine(_directory, "SQLiteBackups"));
        using (var invalid = Open(Path.Combine(_directory, "SQLiteBackups", "invalid_jellyfin.db")))
        {
            Execute(invalid, "PRAGMA foreign_keys=OFF; CREATE TABLE Parent (Id INTEGER PRIMARY KEY); CREATE TABLE Child (ParentId INTEGER REFERENCES Parent(Id)); INSERT INTO Child VALUES (123)");
            Assert.Equal("ok", Scalar(invalid, "PRAGMA integrity_check"));
        }

        var provider = CreateProvider();
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.RestoreBackupFast("invalid", TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.Equal(7L, Scalar(current, "SELECT Id FROM KeepMe"));
    }

    [Fact]
    public async Task Restore_MissingBackupFailsExplicitly()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => CreateProvider().RestoreBackupFast("missing", TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private SqliteDatabaseProvider CreateProvider(IHostApplicationLifetime? lifetime = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_directory);
        return new SqliteDatabaseProvider(paths.Object, NullLogger<SqliteDatabaseProvider>.Instance, lifetime);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL statements in isolated test databases.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL statements in isolated test databases.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command.ExecuteScalar();
    }
}
