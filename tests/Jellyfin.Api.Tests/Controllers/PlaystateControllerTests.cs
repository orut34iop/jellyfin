using System;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class PlaystateControllerTests
{
    [Fact]
    public async Task ReportPlaybackStopped_CancelsSubtitleExtractionForPlaybackSession()
    {
        const string PlaySessionId = "play-session-id";
        var sessionManager = new Mock<ISessionManager>();
        var session = new SessionInfo(sessionManager.Object, Mock.Of<ILogger>()) { Id = "session-id" };
        sessionManager
            .Setup(manager => manager.LogSessionActivity(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Jellyfin.Database.Implementations.Entities.User>()))
            .ReturnsAsync(session);
        sessionManager
            .Setup(manager => manager.OnPlaybackStopped(It.IsAny<PlaybackStopInfo>()))
            .Returns(Task.CompletedTask);

        var transcodeManager = new Mock<ITranscodeManager>();
        transcodeManager
            .Setup(manager => manager.KillTranscodingJobs("device-id", PlaySessionId, It.IsAny<Func<string, bool>>()))
            .Returns(Task.CompletedTask);
        var subtitleEncoder = new Mock<ISubtitleEncoder>();
        var controller = new PlaystateController(
            Mock.Of<IUserManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<ILibraryManager>(),
            sessionManager.Object,
            NullLoggerFactory.Instance,
            transcodeManager.Object,
            subtitleEncoder.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                        [
                            new Claim(InternalClaimTypes.DeviceId, "device-id")
                        ],
                        "Test"))
                }
            }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;

        await controller.ReportPlaybackStopped(new PlaybackStopInfo { PlaySessionId = PlaySessionId });

        subtitleEncoder.Verify(encoder => encoder.CancelSubtitleExtraction(PlaySessionId), Times.Once);
    }
}
