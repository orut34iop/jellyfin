using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library
{
    public class MediaSourceManagerTests
    {
        private readonly MediaSourceManager _mediaSourceManager;

        public MediaSourceManagerTests()
        {
            IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
            _mediaSourceManager = fixture.Create<MediaSourceManager>();
        }

        [Theory]
        [InlineData(@"C:\mydir\myfile.ext", MediaProtocol.File)]
        [InlineData("/mydir/myfile.ext", MediaProtocol.File)]
        [InlineData("file:///mydir/myfile.ext", MediaProtocol.File)]
        [InlineData("http://example.com/stream.m3u8", MediaProtocol.Http)]
        [InlineData("https://example.com/stream.m3u8", MediaProtocol.Http)]
        [InlineData("rtsp://media.example.com:554/twister/audiotrack", MediaProtocol.Rtsp)]
        public void GetPathProtocol_ValidArg_Correct(string path, MediaProtocol expected)
            => Assert.Equal(expected, _mediaSourceManager.GetPathProtocol(path));

        [Fact]
        public async Task GetPlaybackMediaSources_MissingVideoStream_ProbesReturnedMediaSource()
        {
            const long RuntimeTicks = 9_000_000_000;
            const long Size = 123_456_789;

            var mediaSource = new MediaSourceInfo
            {
                Id = "source",
                Path = "/media/movie.mp4",
                Protocol = MediaProtocol.File,
                Type = MediaSourceType.Default,
                ETag = "etag"
            };
            var item = new TestVideo(mediaSource)
            {
                Path = mediaSource.Path
            };

            var libraryManager = new Mock<ILibraryManager>(MockBehavior.Strict);
            libraryManager.Setup(i => i.GetLibraryOptions(item))
                .Returns(new LibraryOptions());
            BaseItem.LibraryManager = libraryManager.Object;

            var providerManager = new Mock<IProviderManager>(MockBehavior.Strict);
            providerManager.Setup(
                    i => i.RefreshSingleItem(
                        item,
                        It.IsAny<MetadataRefreshOptions>(),
                        It.IsAny<CancellationToken>()))
                .ReturnsAsync(ItemUpdateType.None);
            BaseItem.ProviderManager = providerManager.Object;

            var mediaEncoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mediaEncoder.Setup(
                    i => i.GetMediaInfo(
                        It.Is<MediaInfoRequest>(r =>
                            r.MediaSource.Id == mediaSource.Id
                            && r.MediaSource.Path == mediaSource.Path
                            && r.MediaType == MediaBrowser.Model.Dlna.DlnaProfileType.Video
                            && !r.ExtractChapters),
                        It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaInfo
                {
                    RunTimeTicks = RuntimeTicks,
                    Size = Size,
                    Container = "mp4",
                    MediaStreams =
                    [
                        new MediaStream
                        {
                            Type = MediaStreamType.Video,
                            Width = 1920,
                            Height = 1080
                        },
                        new MediaStream
                        {
                            Type = MediaStreamType.Audio,
                            Channels = 2,
                            SampleRate = 48000
                        }
                    ]
                });

            var mediaSourceManager = CreateMediaSourceManager(mediaEncoder.Object);

            var mediaSources = await mediaSourceManager.GetPlaybackMediaSources(item, null, true, false, CancellationToken.None);

            var actual = Assert.Single(mediaSources);
            Assert.Equal(RuntimeTicks, actual.RunTimeTicks);
            Assert.Equal(Size, actual.Size);
            Assert.Equal("mp4", actual.Container);
            Assert.Equal(2, actual.MediaStreams.Count);
            mediaEncoder.Verify(
                i => i.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private static MediaSourceManager CreateMediaSourceManager(IMediaEncoder mediaEncoder)
        {
            IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
            fixture.Inject(mediaEncoder);

            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.Setup(i => i.CachePath)
                .Returns(Path.GetTempPath());
            fixture.Inject(applicationPaths.Object);

            var mediaSourceManager = fixture.Create<MediaSourceManager>();
            mediaSourceManager.AddParts([]);
            return mediaSourceManager;
        }

        private sealed class TestVideo : Video
        {
            private readonly IReadOnlyList<MediaSourceInfo> _mediaSources;

            public TestVideo(MediaSourceInfo mediaSource)
            {
                _mediaSources = [mediaSource];
            }

            public override IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution)
            {
                return _mediaSources;
            }
        }
    }
}
