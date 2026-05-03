using System.Diagnostics;
using Serilog;
using Yarp.ReverseProxy.Transforms;

namespace Mostlylucid.BotDetection.Console.Transforms;

/// <summary>
///     YARP response transform for Stylobot request-completion logging.
///     Security-sensitive response mutation was removed so upstream browser
///     protections and bot verdicts are not exposed to clients.
/// </summary>
public class BotDetectionResponseTransform
{
    /// <summary>
///     Apply bot detection transform to response
/// </summary>
    public async ValueTask TransformAsync(ResponseTransformContext transformContext)
    {
        try
        {
            var httpContext = transformContext.HttpContext;

            // Log request completion
            if (httpContext.Items.TryGetValue("RequestStartTime", out var startTimeObj) &&
                startTimeObj is long startTimestamp)
            {
                var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
                var statusCode = httpContext.Response.StatusCode;

                Log.Information("━━━━━━ REQUEST END ━━━━━━ {Method} {Path} → {StatusCode} in {ElapsedMs:F1}ms",
                    httpContext.Request.Method,
                    httpContext.Request.Path,
                    statusCode,
                    elapsed.TotalMilliseconds);
                Log.Information(""); // Blank line for separation
            }

            await ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in response transform - continuing response");
        }
    }
}
