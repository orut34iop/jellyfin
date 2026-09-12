using System;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Database.Providers.Sqlite;

/// <summary>
/// Stops further EF commands after SQLite reports structural corruption.
/// </summary>
internal sealed class SqliteCorruptionGuard(ILogger logger, IHostApplicationLifetime? lifetime) : DbCommandInterceptor
{
    private Exception? _failure;
    private string? _markerPath;

    public bool IsCorrupted => Volatile.Read(ref _failure) is not null;

    public void Initialize(string markerPath)
    {
        _markerPath = markerPath;
        if (File.Exists(markerPath))
        {
            throw new InvalidOperationException("A previous database corruption failure requires verified recovery before restarting. See " + markerPath);
        }
    }

    public void ObserveFailure(Exception exception)
    {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException)
        {
            if (cause is not SqliteException { SqliteErrorCode: 11 or 26 })
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref _failure, cause, null) is null)
            {
                logger.LogCritical(cause, "SQLite reported database corruption. Blocking further database commands and stopping the server. Preserve the database and journal files; restore only from a verified backup.");
                if (_markerPath is not null)
                {
                    try
                    {
                        File.WriteAllText(_markerPath, "SQLite reported structural corruption. Preserve the database and its journal files. Validate the recovered database (integrity_check, foreign_key_check, user data and library records) before removing this marker and restarting.\n" + cause.Message);
                    }
                    catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
                    {
                        logger.LogCritical(markerException, "Could not persist the database corruption marker. Do not restart without verified recovery.");
                    }
                }

                lifetime?.StopApplication();
            }

            return;
        }
    }

    public void ThrowIfCorrupted()
    {
        var failure = Volatile.Read(ref _failure);
        if (failure is not null)
        {
            throw new InvalidOperationException("Database access is blocked after SQLite reported corruption. Recovery and verification are required before restarting.", failure);
        }
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => ObserveFailure(eventData.Exception);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ObserveFailure(eventData.Exception);
        return Task.CompletedTask;
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        ThrowIfCorrupted();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        => new(ReaderExecuting(command, eventData, result));

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        ThrowIfCorrupted();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        => new(NonQueryExecuting(command, eventData, result));

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        ThrowIfCorrupted();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        => new(ScalarExecuting(command, eventData, result));
}
