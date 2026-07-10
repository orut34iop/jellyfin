using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Middleware;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Middleware;

public class ResponseTimeMiddlewareTests
{
    [Fact]
    public async Task Invoke_WhenResponseIsSlow_LogsWarningWithoutQueryString()
    {
        var logger = new Mock<ILogger<ResponseTimeMiddleware>>();
        var warningLogged = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.Setup(entry => entry.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback(new InvocationAction(invocation =>
                warningLogged.TrySetResult(invocation.Arguments[2].ToString()!)));
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.SetupGet(e => e.Configuration).Returns(new ServerConfiguration
        {
            EnableSlowResponseWarning = true,
            SlowResponseThresholdMs = 50
        });

        using var requestEntered = new ManualResetEventSlim();
        using var releaseRequest = new ManualResetEventSlim();
        using var server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(logger.Object);
                    services.AddSingleton(configurationManager.Object);
                })
                .Configure(app =>
                {
                    app.UseMiddleware<ResponseTimeMiddleware>();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapGet(
                        "/Auth/Keys/{key}",
                        context =>
                        {
                            requestEntered.Set();
                            releaseRequest.Wait(TimeSpan.FromSeconds(5));
                            return context.Response.WriteAsync("ok");
                        }));
                }));
        using var client = server.CreateClient();

        var responseTask = Task.Run(() => client.GetAsync("/Auth/Keys/path-secret?api_key=query-secret"));
        try
        {
            Assert.True(requestEntered.Wait(TimeSpan.FromSeconds(2)));
            var completedTask = await Task.WhenAny(warningLogged.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(warningLogged.Task, completedTask);
            Assert.Contains("is still running", await warningLogged.Task, StringComparison.Ordinal);
        }
        finally
        {
            releaseRequest.Set();
        }

        using var response = await responseTask;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("X-Response-Time-ms"));
        logger.Verify(
            entry => entry.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) =>
                    state.ToString()!.Contains("GET /Auth/Keys/{key}", StringComparison.Ordinal)
                    && !state.ToString()!.Contains("path-secret", StringComparison.Ordinal)
                    && !state.ToString()!.Contains("api_key", StringComparison.Ordinal)
                    && !state.ToString()!.Contains("query-secret", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
