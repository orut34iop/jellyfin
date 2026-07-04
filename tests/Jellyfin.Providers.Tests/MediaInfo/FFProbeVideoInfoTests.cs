using System;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using MediaBrowser.Providers.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo;

public class FFProbeVideoInfoTests
{
    private readonly FFProbeVideoInfo _fFProbeVideoInfo;

    public FFProbeVideoInfoTests()
    {
        var serverConfiguration = new ServerConfiguration()
        {
            DummyChapterDuration = (int)TimeSpan.FromMinutes(5).TotalSeconds
        };
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.Configuration)
            .Returns(serverConfiguration);
        serverConfig.Setup(c => c.GetConfiguration("subtitles"))
            .Returns(new SubtitleOptions());

        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(serverConfig);
        _fFProbeVideoInfo = fixture.Create<FFProbeVideoInfo>();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void CreateDummyChapters_InvalidRuntime_ThrowsArgumentException(long? runtime)
    {
        Assert.Throws<ArgumentException>(
            () => _fFProbeVideoInfo.CreateDummyChapters(new Video()
                {
                    RunTimeTicks = runtime
                }));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0L, 0)]
    [InlineData(1L, 0)]
    [InlineData(TimeSpan.TicksPerMinute * 5, 0)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 50, 10)]
    public void CreateDummyChapters_ValidRuntime_CorrectChaptersCount(long? runtime, int chaptersCount)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
                {
                    RunTimeTicks = runtime
                });

        Assert.Equal(chaptersCount, chapters.Length);
    }

    [Fact]
    public async Task ProbeVideo_LocalMetadataOnlyImport_SkipsMediaEncoderProbe()
    {
        var video = new Video
        {
            Path = "/media/movie.mkv"
        };

        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.Setup(i => i.GetLibraryOptions(video))
            .Returns(new LibraryOptions { LocalMetadataOnlyImport = true });

        var prober = CreateFFProbeVideoInfo(mediaEncoder.Object, libraryManager.Object);
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict));

        var result = await prober.ProbeVideo(video, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        mediaEncoder.Verify(
            i => i.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeVideo_LocalMetadataOnlyImportWithRemoteContentProbe_UsesMediaEncoderProbe()
    {
        const long RuntimeTicks = TimeSpan.TicksPerMinute * 90;

        var video = new Video
        {
            Path = "/media/movie.mkv"
        };
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(i => i.DirectoryExists(It.IsAny<string>()))
            .Returns(false);
        BaseItem.FileSystem = fileSystem.Object;
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(
            i => i.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);

        var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        mediaEncoder.Setup(
                i => i.GetMediaInfo(
                    It.Is<MediaInfoRequest>(r =>
                        r.MediaSource.Path == video.Path
                        && r.MediaSource.Protocol == MediaProtocol.File),
                    It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaBrowser.Model.MediaInfo.MediaInfo
            {
                RunTimeTicks = RuntimeTicks,
                Container = "mkv",
                MediaStreams = new[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video
                    }
                }
            });

        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
        libraryManager.Setup(i => i.GetLibraryOptions(video))
            .Returns(new LibraryOptions { LocalMetadataOnlyImport = true });

        var prober = CreateFFProbeVideoInfo(mediaEncoder.Object, libraryManager.Object, fileSystem.Object);
        var options = new MetadataRefreshOptions(new DirectoryService(fileSystem.Object))
        {
            EnableRemoteContentProbe = true
        };

        var result = await prober.ProbeVideo(video, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Equal(RuntimeTicks, video.RunTimeTicks);
        Assert.Equal("mkv", video.Container);
        mediaEncoder.Verify(
            i => i.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static FFProbeVideoInfo CreateFFProbeVideoInfo(IMediaEncoder mediaEncoder, ILibraryManager libraryManager, IFileSystem? fileSystem = null)
    {
        var serverConfiguration = new ServerConfiguration()
        {
            DummyChapterDuration = (int)TimeSpan.FromMinutes(5).TotalSeconds
        };
        var serverConfig = new Mock<IServerConfigurationManager>();
        serverConfig.Setup(c => c.Configuration)
            .Returns(serverConfiguration);
        serverConfig.Setup(c => c.GetConfiguration("subtitles"))
            .Returns(new SubtitleOptions());

        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(serverConfig);
        fixture.Inject(mediaEncoder);
        fixture.Inject(libraryManager);
        if (fileSystem is not null)
        {
            fixture.Inject(fileSystem);
        }

        return fixture.Create<FFProbeVideoInfo>();
    }
}
