using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class SubtitleControllerTests
{
    [Fact]
    public async Task GetSubtitle_PassesPlaybackSessionAndRequestAbortedTokenToEncoder()
    {
        var itemId = Guid.NewGuid();
        var item = new Movie { Id = itemId };
        using var cancellationTokenSource = new CancellationTokenSource();
        CancellationToken receivedCancellationToken = default;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(manager => manager.GetItemById<BaseItem>(itemId)).Returns(item);

        var subtitleEncoder = new Mock<ISubtitleEncoder>();
        subtitleEncoder
            .Setup(encoder => encoder.GetSubtitles(
                item,
                "media-source-id",
                2,
                "srt",
                0,
                0,
                false,
                "play-session-id",
                It.IsAny<CancellationToken>()))
            .Callback<BaseItem, string, int, string, long, long, bool, string, CancellationToken>(
                (_, _, _, _, _, _, _, _, cancellationToken) => receivedCancellationToken = cancellationToken)
            .ReturnsAsync(new MemoryStream([1]));

        var controller = new SubtitleController(
            Mock.Of<IServerConfigurationManager>(),
            libraryManager.Object,
            Mock.Of<ISubtitleManager>(),
            subtitleEncoder.Object,
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IProviderManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<ILogger<SubtitleController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestAborted = cancellationTokenSource.Token
                }
            }
        };

        await controller.GetSubtitle(
            itemId,
            "media-source-id",
            2,
            "srt",
            null,
            null,
            null,
            null,
            null,
            playSessionId: "play-session-id");

        Assert.Equal(cancellationTokenSource.Token, receivedCancellationToken);
    }
}
