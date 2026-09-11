using System;
using System.IO;
using Emby.Naming.Common;
using Emby.Naming.Video;
using Emby.Server.Implementations.Library.Resolvers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class BaseVideoResolverTests
{
    [Fact]
    public void SetVideoType_DefaultOptionsForMissingIso_AttemptsUdfProbe()
    {
        var logger = new Mock<ILogger>();
        var resolver = new TestVideoResolver(logger.Object);
        var video = new Video
        {
            Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".iso")
        };

        resolver.SetVideoTypeForTest(video, new LibraryOptions());

        Assert.Equal(VideoType.Iso, video.VideoType);
        VerifyLog(logger, LogLevel.Error, Times.Once());
    }

    [Fact]
    public void SetVideoType_LocalMetadataOnlyImportIso_SkipsUdfProbe()
    {
        var logger = new Mock<ILogger>();
        var resolver = new TestVideoResolver(logger.Object);
        var video = new Video
        {
            Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".iso")
        };

        resolver.SetVideoTypeForTest(video, new LibraryOptions { LocalMetadataOnlyImport = true });

        Assert.Equal(VideoType.Iso, video.VideoType);
        Assert.Null(video.IsoType);
        VerifyLog(logger, LogLevel.Error, Times.Never());
    }

    private static void VerifyLog(Mock<ILogger> logger, LogLevel level, Times times)
        => logger.Verify(
            x => x.Log(
                level,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((_, _) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            times);

    private sealed class TestVideoResolver : BaseVideoResolver<Video>
    {
        public TestVideoResolver(ILogger logger)
            : base(logger, new NamingOptions(), Mock.Of<IDirectoryService>())
        {
        }

        public void SetVideoTypeForTest(Video video, LibraryOptions libraryOptions)
            => SetVideoType(video, new VideoFileInfo("movie", video.Path, "iso"), libraryOptions);
    }
}
