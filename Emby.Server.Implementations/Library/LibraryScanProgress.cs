using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// Serializes scan reports and ignores delayed callbacks from an earlier phase.
/// </summary>
internal sealed class LibraryScanProgress(IProgress<double> progress, ILogger logger, CancellationToken cancellationToken)
{
    private readonly object _sync = new();
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _lastLog = Stopwatch.GetTimestamp();
    private double _lastLoggedProgress = -1;
    private double _percent;
    private int _stage;
    private string _phase = "initializing";
    private bool _stopped;

    public IProgress<double> CreatePhase(int stage, string phase, double offset, double scale)
    {
        Report(stage, offset, phase);
        return new PhaseProgress(this, stage, phase, offset, scale);
    }

    public void Report(int stage, double percent, string phase)
    {
        lock (_sync)
        {
            if (_stopped || cancellationToken.IsCancellationRequested || stage < _stage || !double.IsFinite(percent))
            {
                return;
            }

            var phaseChanged = stage > _stage;
            _stage = stage;
            _phase = phase;
            _percent = Math.Max(_percent, Math.Clamp(percent, 0, 100));
            progress.Report(_percent);

            if (phaseChanged || _percent >= _lastLoggedProgress + 1 || Stopwatch.GetElapsedTime(_lastLog).TotalSeconds >= 60)
            {
                Log(false);
                _lastLoggedProgress = _percent;
                _lastLog = Stopwatch.GetTimestamp();
            }
        }
    }

    public void Heartbeat()
    {
        lock (_sync)
        {
            if (!_stopped && !cancellationToken.IsCancellationRequested)
            {
                Log(true);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _stopped = true;
        }
    }

    private void Log(bool heartbeat)
    {
        var elapsed = Stopwatch.GetElapsedTime(_start);
        if (heartbeat)
        {
            logger.LogInformation(
                "Media library scan heartbeat {Progress:F1}% phase {Phase}; elapsed {Minutes} minute(s) {Seconds} seconds",
                _percent, _phase, Math.Truncate(elapsed.TotalMinutes), elapsed.Seconds);
        }
        else
        {
            logger.LogInformation(
                "Media library scan progress {Progress:F1}% phase {Phase}; elapsed {Minutes} minute(s) {Seconds} seconds",
                _percent, _phase, Math.Truncate(elapsed.TotalMinutes), elapsed.Seconds);
        }
    }

    private sealed class PhaseProgress(LibraryScanProgress owner, int stage, string phase, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => owner.Report(stage, offset + (value * scale), phase);
    }
}
