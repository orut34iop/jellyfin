using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class LibraryScanProgressTests
{
    [Fact]
    public void DelayedValidationReport_DoesNotOverwritePostScanProgress()
    {
        var sink = new RecordingProgress();
        var progress = new LibraryScanProgress(sink, NullLogger.Instance, CancellationToken.None);
        var validation = progress.CreatePhase(2, "validation", 0, 0.96);
        validation.Report(50);
        var postScan = progress.CreatePhase(4, "post-scan", 96, 0.04);
        postScan.Report(50);
        validation.Report(100);
        Assert.Equal(new double[] { 0, 48, 96, 98 }, sink.Values);
    }

    [Fact]
    public void ConcurrentReports_NeverMoveProgressBackwards()
    {
        var sink = new RecordingProgress();
        var progress = new LibraryScanProgress(sink, NullLogger.Instance, CancellationToken.None);
        var phase = progress.CreatePhase(2, "validation", 0, 1);
        Parallel.For(0, 1000, i => phase.Report(i % 100));
        for (var i = 1; i < sink.Values.Count; i++)
        {
            Assert.True(sink.Values[i] >= sink.Values[i - 1]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelledOrStoppedScan_IgnoresQueuedCompletion(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new RecordingProgress();
        var progress = new LibraryScanProgress(sink, NullLogger.Instance, cancellation.Token);
        var phase = progress.CreatePhase(2, "validation", 0, 0.96);
        phase.Report(50);
        if (cancel)
        {
            cancellation.Cancel();
        }
        else
        {
            progress.Stop();
        }

        phase.Report(100);
        progress.Report(5, 100, "completed");
        Assert.Equal(new double[] { 0, 48 }, sink.Values);
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
}
