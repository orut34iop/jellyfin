using System;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// Coordinates metadata edits and provider refreshes for the same item without holding a database transaction.
/// </summary>
public static class MetadataUpdateLock
{
    private static readonly AsyncKeyedLocker<Guid> Locks = new();

    /// <summary>
    /// Acquires exclusive access to an item's metadata. Callers must not acquire the same item recursively.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A lease that releases access when disposed.</returns>
    public static async ValueTask<IDisposable> AcquireAsync(Guid itemId, CancellationToken cancellationToken)
        => await Locks.LockAsync(itemId, cancellationToken).ConfigureAwait(false);
}
