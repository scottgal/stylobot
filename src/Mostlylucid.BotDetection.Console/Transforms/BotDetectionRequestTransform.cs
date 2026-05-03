using System.Diagnostics;
using Mostlylucid.BotDetection.Console.Extensions;
using Mostlylucid.BotDetection.Console.Logging;
using Mostlylucid.BotDetection.Console.Models;
using Serilog;
using Yarp.ReverseProxy.Transforms;

namespace Mostlylucid.BotDetection.Console.Transforms;

/// <summary>
///     YARP request transform for bot detection integration
/// </summary>
public class BotDetectionRequestTransform
{
    private readonly SignatureLoggingConfig _config;
    private readonly string _mode;
    private readonly SignatureLogger _signatureLogger;

    public BotDetectionRequestTransform(string mode, SignatureLoggingConfig config, SignatureLogger signatureLogger)
    {
        _mode = mode;
        _config = config;
        _signatureLogger = signatureLogger;
    }

    /// <summary>
    ///     Apply bot detection transform to request
    /// </summary>
    public async ValueTask TransformAsync(RequestTransformContext transformContext)
    {
        try
        {
            var httpContext = transformContext.HttpContext;

            // Mark request start time and log request beginning
            var requestStartTime = Stopwatch.GetTimestamp();
            httpContext.Items["RequestStartTime"] = requestStartTime;

            Log.Information("━━━━━━ REQUEST START ━━━━━━ {Method} {Path} from {IP}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.Connection.RemoteIpAddress);

            // Get detection result using convenient extension method
            var detection = httpContext.GetBotDetectionResult();
            if (detection != null)
            {
                var detectionTime = httpContext.BotDetectionTime() ?? TimeSpan.Zero;

                // Log full detection info (mode-dependent verbosity)
                if (_mode.Equals("demo", StringComparison.OrdinalIgnoreCase))
                {
                    DetectionLogger.LogDetectionDemo(httpContext, detection, detectionTime, _config);
                }
                else
                {
                    // Production: Only log bot detections and blocks
                    if (httpContext.IsBot() || httpContext.WasBlocked())
                        DetectionLogger.LogDetectionProduction(httpContext, detection, detectionTime, _config);
                }

                // Log signature in JSON-LD format if enabled and meets confidence threshold
                if (_config.Enabled && httpContext.IsBot() && httpContext.BotConfidenceScore() >= _config.MinConfidence)
                    _signatureLogger.LogSignatureJsonLd(httpContext, detection, _config);

                // Forward detection headers to upstream
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Bot-Detection",
                    httpContext.IsBot().ToString());
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Bot-Probability",
                    httpContext.BotConfidenceScore().ToString("F2"));

                if (detectionTime != TimeSpan.Zero)
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Bot-DetectionTime",
                        detectionTime.TotalMilliseconds.ToString("F0"));

                var botName = httpContext.BotName();
                if (!string.IsNullOrEmpty(botName))
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Bot-Name", botName);

                var botType = httpContext.BotType();
                if (botType.HasValue)
                    transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Bot-Type",
                        botType.Value.ToString());
            }

            await ValueTask.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in request transform - continuing request");
        }
    }
}