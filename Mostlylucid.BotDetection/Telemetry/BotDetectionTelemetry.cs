using System.Diagnostics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     Telemetry instrumentation for bot detection operations
/// </summary>
public static class BotDetectionTelemetry
{
    /// <summary>
    ///     Activity source name for bot detection
    /// </summary>
    public const string ActivitySourceName = "Mostlylucid.BotDetection";

    /// <summary>
    ///     Activity source for bot detection telemetry
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, GetVersion());

    private static string GetVersion()
    {
        return typeof(BotDetectionTelemetry).Assembly.GetName().Version?.ToString() ?? "1.0.0";
    }

    /// <summary>
    ///     Starts an activity for bot detection
    /// </summary>
    public static Activity? StartDetectActivity(string? clientIp = null, string? userAgent = null)
    {
        var activity = ActivitySource.StartActivity("BotDetection.Detect");

        if (activity != null)
        {
            if (clientIp != null)
                activity.SetTag("http.client_ip", clientIp);
            if (userAgent != null)
                activity.SetTag("http.user_agent", userAgent);
        }

        return activity;
    }

    /// <summary>
    ///     Records bot detection result on the activity
    /// </summary>
    public static void RecordResult(Activity? activity, BotDetectionResult result)
    {
        if (activity == null)
            return;

        activity.SetTag("mostlylucid.botdetection.is_bot", result.IsBot);
        activity.SetTag("mostlylucid.botdetection.confidence", result.ConfidenceScore);
        activity.SetTag("mostlylucid.botdetection.processing_time_ms", result.ProcessingTimeMs);

        if (result.BotType.HasValue)
            activity.SetTag("mostlylucid.botdetection.bot_type", result.BotType.Value.ToString());

        if (!string.IsNullOrEmpty(result.BotName))
            activity.SetTag("mostlylucid.botdetection.bot_name", result.BotName);

        if (result.Reasons.Count > 0)
            activity.SetTag("mostlylucid.botdetection.reason_count", result.Reasons.Count);

        activity.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    ///     Records an exception on the activity
    /// </summary>
    public static void RecordException(Activity? activity, Exception ex)
    {
        if (activity == null)
            return;

        activity.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity.SetTag("exception.type", ex.GetType().FullName);
        activity.SetTag("exception.message", ex.Message);
    }
}