using System;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
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
    [InlineData(1L, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 3, 1)]
    [InlineData(TimeSpan.TicksPerMinute * 5, 1)]
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

    [Theory]
    [InlineData(1L)]
    [InlineData(TimeSpan.TicksPerMinute * 3)]
    [InlineData(TimeSpan.TicksPerMinute * 5)]
    [InlineData((TimeSpan.TicksPerMinute * 5) + 1)]
    [InlineData((TimeSpan.TicksPerMinute * 50) + 1)]
    public void CreateDummyChapters_PositiveRuntime_NoChapterBeyondRuntime(long runtime)
    {
        var chapters = _fFProbeVideoInfo.CreateDummyChapters(new Video()
        {
            RunTimeTicks = runtime
        });

        Assert.All(chapters, chapter => Assert.True(chapter.StartPositionTicks < runtime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FetchEmbeddedInfo_NoExtra_AppliesContainerDates(bool replaceAllMetadata)
    {
        var video = new Video();

        _fFProbeVideoInfo.FetchEmbeddedInfo(video, CreateMediaInfoWithDates(), CreateRefreshOptions(replaceAllMetadata), new LibraryOptions());

        Assert.Equal(2016, video.ProductionYear);
        Assert.Equal(new DateTime(2016, 5, 4, 0, 0, 0, DateTimeKind.Utc), video.PremiereDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FetchEmbeddedInfo_PlaybackProbe_PreservesEditedAndEmptyFields(bool emptyGenres)
    {
        var video = new Video { Name = "Edited title", Genres = emptyGenres ? [] : ["Edited genre"] };
        var data = CreateMediaInfoWithDates();
        data.Name = "Embedded title";
        data.Genres = ["Embedded genre"];
        var options = CreateRefreshOptions(false);
        options.RefreshMediaInfoOnly = true;

        _fFProbeVideoInfo.FetchEmbeddedInfo(video, data, options, new LibraryOptions { EnableEmbeddedTitles = true });

        Assert.Equal("Edited title", video.Name);
        Assert.Equal(emptyGenres ? Array.Empty<string>() : ["Edited genre"], video.Genres);
        Assert.Null(video.ProductionYear);
        Assert.Null(video.PremiereDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FetchEmbeddedInfo_Extra_IgnoresContainerDates(bool replaceAllMetadata)
    {
        var video = new Video
        {
            ExtraType = ExtraType.Trailer,
            ProductionYear = 1982,
            PremiereDate = new DateTime(1982, 6, 25, 0, 0, 0, DateTimeKind.Utc)
        };

        _fFProbeVideoInfo.FetchEmbeddedInfo(video, CreateMediaInfoWithDates(), CreateRefreshOptions(replaceAllMetadata), new LibraryOptions());

        Assert.Equal(1982, video.ProductionYear);
        Assert.Equal(new DateTime(1982, 6, 25, 0, 0, 0, DateTimeKind.Utc), video.PremiereDate);
    }

    [Fact]
    public void FetchEmbeddedInfo_ExtraWithoutDates_StaysWithoutDates()
    {
        var video = new Video
        {
            ExtraType = ExtraType.Trailer
        };

        _fFProbeVideoInfo.FetchEmbeddedInfo(video, CreateMediaInfoWithDates(), CreateRefreshOptions(false), new LibraryOptions());

        Assert.Null(video.ProductionYear);
        Assert.Null(video.PremiereDate);
    }

    private static MediaBrowser.Model.MediaInfo.MediaInfo CreateMediaInfoWithDates()
        => new()
        {
            ProductionYear = 2016,
            PremiereDate = new DateTime(2016, 5, 4, 0, 0, 0, DateTimeKind.Utc)
        };

    private static MetadataRefreshOptions CreateRefreshOptions(bool replaceAllMetadata)
        => new(Mock.Of<IDirectoryService>())
        {
            ReplaceAllMetadata = replaceAllMetadata
        };

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeVideo_LocalMetadataOnlyImportWithRemoteContentProbe_UsesMediaEncoderProbe(bool mediaInfoOnly)
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
                Genres = ["Embedded genre"],
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
            EnableRemoteContentProbe = true,
            RefreshMediaInfoOnly = mediaInfoOnly
        };

        var result = await prober.ProbeVideo(video, options, CancellationToken.None);

        Assert.Equal(ItemUpdateType.MetadataImport, result);
        Assert.Equal(RuntimeTicks, video.RunTimeTicks);
        Assert.Equal("mkv", video.Container);
        Assert.Equal(mediaInfoOnly ? Array.Empty<string>() : ["Embedded genre"], video.Genres);
        mediaEncoder.Verify(
            i => i.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeVideo_ExternalStreamsOnly_PreservesEmbeddedStreamsChaptersAndAttachments(bool localOnly)
    {
        var videoMock = new Mock<Video> { CallBase = true };
        var video = videoMock.Object;
        video.Id = Guid.NewGuid();
        video.Path = "/media/movie.mkv";
        video.RunTimeTicks = TimeSpan.TicksPerHour;
        video.Genres = ["Edited"];
        var embedded = new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080 };
        var removed = new MediaStream { Type = MediaStreamType.Subtitle, IsExternal = true, Path = "/media/removed.srt" };
        videoMock.Setup(v => v.GetMediaStreams()).Returns([embedded, removed]);
        var fileSystem = new Mock<IFileSystem>();
        fileSystem.Setup(f => f.GetFileSystemEntryPaths(It.IsAny<string>())).Returns(["/media/movie.mkv"]);
        BaseItem.FileSystem = fileSystem.Object;
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>(m => m.GetPathProtocol(It.IsAny<string>()) == MediaProtocol.File);
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetLibraryOptions(video)).Returns(new LibraryOptions { LocalMetadataOnlyImport = localOnly });
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var attachments = new Mock<IMediaAttachmentRepository>(MockBehavior.Strict);
        var chapters = new Mock<IChapterManager>(MockBehavior.Strict);
        var streams = new Mock<IMediaStreamRepository>();
        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(fileSystem.Object);
        fixture.Inject(library.Object);
        fixture.Inject(encoder.Object);
        fixture.Inject(attachments.Object);
        fixture.Inject(chapters.Object);
        fixture.Inject(streams.Object);
        var prober = fixture.Create<FFProbeVideoInfo>();
        var options = MetadataRefreshOptions.ForExternalStreams(new DirectoryService(fileSystem.Object));
        await prober.ProbeVideo(video, options, CancellationToken.None);
        Assert.Equal(TimeSpan.TicksPerHour, video.RunTimeTicks);
        Assert.Equal(["Edited"], video.Genres);
        Assert.False(video.HasSubtitles);
        streams.Verify(r => r.SaveMediaStreams(video.Id, It.Is<System.Collections.Generic.IReadOnlyList<MediaStream>>(s => s.Count == 1 && s[0].Codec == "h264"), It.IsAny<CancellationToken>()), Times.Once);
        encoder.VerifyNoOtherCalls();
        attachments.VerifyNoOtherCalls();
        chapters.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeVideo_InvalidOrObsoleteResult_DoesNotReplaceExistingInformation(bool changedDuringProbe)
    {
        var video = new Video { Id = Guid.NewGuid(), Path = "/media/movie.mkv", RunTimeTicks = 123, Genres = ["Edited"] };
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetLibraryOptions(video)).Returns(new LibraryOptions());
        var encoder = new Mock<IMediaEncoder>();
        encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (changedDuringProbe)
                {
                    video.Path = "/media/replacement.mkv";
                }
            })
            .ReturnsAsync(new MediaBrowser.Model.MediaInfo.MediaInfo
            {
                RunTimeTicks = 456,
                MediaStreams = changedDuringProbe ? [new MediaStream { Type = MediaStreamType.Video }] : []
            });
        var prober = CreateFFProbeVideoInfo(encoder.Object, library.Object);
        var options = new MetadataRefreshOptions(Mock.Of<IDirectoryService>(MockBehavior.Strict)) { RefreshMediaInfoOnly = true };
        Assert.Equal(ItemUpdateType.None, await prober.ProbeVideo(video, options, CancellationToken.None));
        Assert.Equal(123, video.RunTimeTicks);
        Assert.Equal(["Edited"], video.Genres);
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
