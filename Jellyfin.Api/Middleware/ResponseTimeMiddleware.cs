using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Response time middleware.
/// </summary>
public class ResponseTimeMiddleware
{
    private const string ResponseHeaderResponseTime = "X-Response-Time-ms";

    private readonly RequestDelegate _next;
    private readonly ILogger<ResponseTimeMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseTimeMiddleware"/> class.
    /// </summary>
    /// <param name="next">Next request delegate.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ExceptionMiddleware}"/> interface.</param>
    public ResponseTimeMiddleware(
        RequestDelegate next,
        ILogger<ResponseTimeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Invoke request.
    /// </summary>
    /// <param name="context">Request context.</param>
    /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <returns>Task.</returns>
    public async Task Invoke(HttpContext context, IServerConfigurationManager serverConfigurationManager)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var warningLogged = 0;
        var requestCompleted = 0;

        var enableWarning = serverConfigurationManager.Configuration.EnableSlowResponseWarning;
        var warningThreshold = serverConfigurationManager.Configuration.SlowResponseThresholdMs;
        context.Response.OnStarting(() =>
        {
            var responseTime = Stopwatch.GetElapsedTime(startTimestamp);
            var responseTimeMs = responseTime.TotalMilliseconds;
            if (enableWarning
                && responseTimeMs > warningThreshold
                && Interlocked.Exchange(ref warningLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Slow HTTP Response from {Method} {Route} to {RemoteIP} in {Elapsed:g} with Status Code {StatusCode}",
                    context.Request.Method,
                    GetRouteTemplate(context),
                    context.GetNormalizedRemoteIP(),
                    responseTime,
                    context.Response.StatusCode);
            }

            context.Response.Headers[ResponseHeaderResponseTime] = responseTimeMs.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });

        using var warningCancellationTokenSource = new CancellationTokenSource();
        var warningTask = enableWarning
            ? WarnIfResponseHasNotStartedAsync(warningCancellationTokenSource.Token)
            : Task.CompletedTask;

        try
        {
            // Call the next delegate/middleware in the pipeline.
            // The warning task is started first so synchronous downstream work can
            // still be observed while the current request thread is blocked.
            await this._next(context).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref requestCompleted, 1);
            await warningCancellationTokenSource.CancelAsync().ConfigureAwait(false);
            await warningTask.ConfigureAwait(false);
        }

        async Task WarnIfResponseHasNotStartedAsync(CancellationToken cancellationToken)
        {
            var warningDelayMs = Math.Clamp(warningThreshold, 0, int.MaxValue);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(warningDelayMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!context.Response.HasStarted
                && Volatile.Read(ref requestCompleted) == 0
                && Interlocked.Exchange(ref warningLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Slow HTTP Request from {Method} {Route} to {RemoteIP} is still running after {Elapsed:g}",
                    context.Request.Method,
                    GetRouteTemplate(context),
                    context.GetNormalizedRemoteIP(),
                    Stopwatch.GetElapsedTime(startTimestamp));
            }
        }
    }

    private static string GetRouteTemplate(HttpContext context)
    {
        var routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (string.IsNullOrEmpty(routeTemplate))
        {
            return "<unmatched>";
        }

        return routeTemplate[0] == '/' ? routeTemplate : "/" + routeTemplate;
    }
}
