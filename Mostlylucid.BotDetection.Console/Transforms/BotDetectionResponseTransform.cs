using System.Diagnostics;
using Mostlylucid.BotDetection.Console.Extensions;
using Serilog;
using Yarp.ReverseProxy.Transforms;

namespace Mostlylucid.BotDetection.Console.Transforms;

/// <summary>
///     YARP response transform for bot detection integration and CSP handling
/// </summary>
public class BotDetectionResponseTransform
{
    private readonly string _mode;

    public BotDetectionResponseTransform(string mode)
    {
        _mode = mode;
    }

    /// <summary>
    ///     Apply bot detection transform to response
    /// </summary>
    public async ValueTask TransformAsync(ResponseTransformContext transformContext)
    {
        try
        {
            var httpContext = transformContext.HttpContext;

            // Add bot detection callback URL header for client-side tag
            var callbackUrl =
                $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/api/bot-detection/client-result";
            httpContext.Response.Headers.TryAdd("X-Bot-Detection-Callback-Url", callbackUrl);

            // Pass through bot detection headers to client using convenient extension methods
            if (httpContext.GetBotDetectionResult() != null)
            {
                httpContext.Response.Headers.TryAdd("X-Bot-Detection", httpContext.IsBot().ToString());
                httpContext.Response.Headers.TryAdd("X-Bot-Probability",
                    httpContext.BotConfidenceScore().ToString("F2"));

                var botName = httpContext.BotName();
                if (!string.IsNullOrEmpty(botName)) httpContext.Response.Headers.TryAdd("X-Bot-Name", botName);
            }

            // PRODUCTION MODE: Remove CSP headers to allow client-side bot detection
            // DEMO MODE: Pass through CSP headers to preserve upstream security policies
            if (!_mode.Equals("demo", StringComparison.OrdinalIgnoreCase))
            {
                // Remove Content-Security-Policy from both proxy response AND final response headers
                if (transformContext.ProxyResponse?.Headers.Contains("Content-Security-Policy") == true)
                    transformContext.ProxyResponse.Headers.Remove("Content-Security-Policy");
                httpContext.Response.Headers.Remove("Content-Security-Policy");

                // Remove Content-Security-Policy-Report-Only
                if (transformContext.ProxyResponse?.Headers.Contains("Content-Security-Policy-Report-Only") == true)
                    transformContext.ProxyResponse.Headers.Remove("Content-Security-Policy-Report-Only");
                httpContext.Response.Headers.Remove("Content-Security-Policy-Report-Only");

                // Remove X-Frame-Options to allow embedding
                if (transformContext.ProxyResponse?.Headers.Contains("X-Frame-Options") == true)
                    transformContext.ProxyResponse.Headers.Remove("X-Frame-Options");
                httpContext.Response.Headers.Remove("X-Frame-Options");
            }

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