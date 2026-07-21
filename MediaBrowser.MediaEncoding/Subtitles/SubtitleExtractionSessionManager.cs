using System;
using System.Collections.Generic;
using System.Threading;

namespace MediaBrowser.MediaEncoding.Subtitles;

internal sealed class SubtitleExtractionSessionManager : IDisposable
{
    private static readonly TimeSpan StoppedSessionRetention = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InactiveSessionRetention = TimeSpan.FromHours(2);

    private readonly object _syncLock = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public Registration Register(string? playSessionId, CancellationToken requestCancellationToken)
    {
        if (string.IsNullOrWhiteSpace(playSessionId))
        {
            return new Registration(null, null, CancellationTokenSource.CreateLinkedTokenSource(requestCancellationToken));
        }

        lock (_syncLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpiredSessions(DateTime.UtcNow);

            if (!_sessions.TryGetValue(playSessionId, out var state))
            {
                state = new SessionState();
                _sessions.Add(playSessionId, state);
            }

            if (state.StoppedAtUtc.HasValue)
            {
                throw new OperationCanceledException($"Playback session {playSessionId} has stopped.");
            }

            state.ActiveRegistrations++;
            state.LastAccessUtc = DateTime.UtcNow;
            var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellationToken,
                state.CancellationTokenSource.Token);

            return new Registration(this, playSessionId, linkedCancellationTokenSource);
        }
    }

    public void Cancel(string playSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playSessionId);

        CancellationTokenSource cancellationTokenSource;
        lock (_syncLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RemoveExpiredSessions(DateTime.UtcNow);

            if (!_sessions.TryGetValue(playSessionId, out var state))
            {
                state = new SessionState();
                _sessions.Add(playSessionId, state);
            }

            state.StoppedAtUtc ??= DateTime.UtcNow;
            state.LastAccessUtc = DateTime.UtcNow;
            cancellationTokenSource = state.CancellationTokenSource;
        }

        cancellationTokenSource.Cancel();
    }

    public void Dispose()
    {
        SessionState[] states;
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            states = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var state in states)
        {
            state.CancellationTokenSource.Cancel();
            state.CancellationTokenSource.Dispose();
        }
    }

    private void Release(string playSessionId)
    {
        lock (_syncLock)
        {
            if (_disposed || !_sessions.TryGetValue(playSessionId, out var state))
            {
                return;
            }

            state.ActiveRegistrations--;
            state.LastAccessUtc = DateTime.UtcNow;
            RemoveExpiredSessions(state.LastAccessUtc);
        }
    }

    private void RemoveExpiredSessions(DateTime nowUtc)
    {
        List<string>? expiredSessionIds = null;
        foreach (var session in _sessions)
        {
            var state = session.Value;
            if (state.ActiveRegistrations != 0)
            {
                continue;
            }

            var retention = state.StoppedAtUtc.HasValue ? StoppedSessionRetention : InactiveSessionRetention;
            if (nowUtc - state.LastAccessUtc < retention)
            {
                continue;
            }

            expiredSessionIds ??= [];
            expiredSessionIds.Add(session.Key);
        }

        if (expiredSessionIds is null)
        {
            return;
        }

        foreach (var playSessionId in expiredSessionIds)
        {
            var state = _sessions[playSessionId];
            _sessions.Remove(playSessionId);
            state.CancellationTokenSource.Dispose();
        }
    }

    private sealed class SessionState
    {
        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public int ActiveRegistrations { get; set; }

        public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;

        public DateTime? StoppedAtUtc { get; set; }
    }

    internal sealed class Registration : IDisposable
    {
        private readonly SubtitleExtractionSessionManager? _manager;
        private readonly string? _playSessionId;
        private CancellationTokenSource? _linkedCancellationTokenSource;

        public Registration(
            SubtitleExtractionSessionManager? manager,
            string? playSessionId,
            CancellationTokenSource linkedCancellationTokenSource)
        {
            _manager = manager;
            _playSessionId = playSessionId;
            _linkedCancellationTokenSource = linkedCancellationTokenSource;
        }

        public CancellationToken CancellationToken => _linkedCancellationTokenSource?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            var linkedCancellationTokenSource = Interlocked.Exchange(ref _linkedCancellationTokenSource, null);
            if (linkedCancellationTokenSource is null)
            {
                return;
            }

            linkedCancellationTokenSource.Dispose();
            if (_manager is not null && _playSessionId is not null)
            {
                _manager.Release(_playSessionId);
            }
        }
    }
}
