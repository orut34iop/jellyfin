using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public static class StreamingHelpersTests
    {
        [Theory]
        [InlineData("/media/show/episode.mp4", "mov,mp4,m4a,3gp,3g2,mj2", ".mp4")]
        [InlineData("/media/show/episode.MP4", "mov, mp4, m4a, 3gp, 3g2, mj2", ".MP4")]
        [InlineData("/media/show/episode.m4v", "mov,mp4,m4a,3gp,3g2,mj2", ".mov")]
        public static void GetOutputFileExtension_NoRequestedExtension_PrefersMatchingMediaSourceFileExtension(string path, string container, string expected)
        {
            var state = CreateVideoStreamState();
            var mediaSource = new MediaSourceInfo
            {
                Path = path,
                Container = container
            };

            Assert.Equal(expected, StreamingHelpers.GetOutputFileExtension(state, mediaSource));
        }

        [Fact]
        public static void GetOutputFileExtension_RequestedExtension_ReturnsRequestedExtension()
        {
            var state = CreateVideoStreamState();
            state.RequestedUrl = "stream.mkv";

            var mediaSource = new MediaSourceInfo
            {
                Path = "/media/show/episode.mp4",
                Container = "mov,mp4,m4a,3gp,3g2,mj2"
            };

            Assert.Equal(".mkv", StreamingHelpers.GetOutputFileExtension(state, mediaSource));
        }

        private static StreamState CreateVideoStreamState()
        {
            return new StreamState(
                Mock.Of<IMediaSourceManager>(),
                TranscodingJobType.Progressive,
                Mock.Of<ITranscodeManager>())
            {
                RequestedUrl = "stream",
                Request = new VideoRequestDto()
            };
        }
    }
}
