using System;
using System.Threading;
using MediaBrowser.MediaEncoding.Subtitles;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Subtitles;

public class SubtitleExtractionSessionManagerTests
{
    [Fact]
    public void Cancel_ActiveRegistration_CancelsToken()
    {
        using var manager = new SubtitleExtractionSessionManager();
        using var registration = manager.Register("play-session-id", CancellationToken.None);

        manager.Cancel("play-session-id");

        Assert.True(registration.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Register_StoppedSession_ThrowsOperationCanceledException()
    {
        using var manager = new SubtitleExtractionSessionManager();
        manager.Cancel("play-session-id");

        Assert.Throws<OperationCanceledException>(() => manager.Register("play-session-id", CancellationToken.None));
    }

    [Fact]
    public void Cancel_DifferentSession_DoesNotCancelToken()
    {
        using var manager = new SubtitleExtractionSessionManager();
        using var registration = manager.Register("active-session-id", CancellationToken.None);

        manager.Cancel("stopped-session-id");

        Assert.False(registration.CancellationToken.IsCancellationRequested);
    }
}
