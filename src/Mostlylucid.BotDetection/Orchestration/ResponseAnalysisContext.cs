using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Context prepared during REQUEST analysis that configures RESPONSE analysis.
///     This is created by early detectors (first wave) and stored in HttpContext.Items.
///     Response coordinator reads this to know HOW to analyze the response.
/// </summary>
public sealed class ResponseAnalysisContext
{
    /// <summary>
    ///     HttpContext.Items key for storing this context
    /// </summary>
    public const string HttpContextKey = "BotDetection.ResponseAnalysisContext";

    /// <summary>
    ///     Should response be analyzed at all?
    /// </summary>
    public bool EnableAnalysis { get; set; }

    /// <summary>
    ///     Mode: Async (after response sent) or Inline (during response)
    /// </summary>
    public ResponseAnalysisMode Mode { get; set; } = ResponseAnalysisMode.Async;

    /// <summary>
    ///     Should response body be streamed for real-time analysis?
    ///     Only applicable in Inline mode.
    /// </summary>
    public bool EnableStreaming { get; set; }

    /// <summary>
    ///     Thoroughness level based on request-side risk.
    ///     Higher risk = more thorough analysis.
    /// </summary>
    public ResponseAnalysisThoroughness Thoroughness { get; set; } = ResponseAnalysisThoroughness.Standard;

    /// <summary>
    ///     Which response detectors to run (can be subset for performance)
    /// </summary>
    public HashSet<string> EnabledDetectors { get; set; } = new();

    /// <summary>
    ///     Request-side signals that triggered response analysis.
    ///     These can inform response detector logic.
    /// </summary>
    public Dictionary<string, object> TriggerSignals { get; set; } = new();

    /// <summary>
    ///     Client ID (hash) from request side
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    ///     Request-side bot probability (0.0-1.0)
    /// </summary>
    public double RequestBotProbability { get; set; }

    /// <summary>
    ///     Was this triggered by a specific detector?
    /// </summary>
    public string? TriggeringDetector { get; set; }

    /// <summary>
    ///     Reason for triggering response analysis
    /// </summary>
    public string? TriggerReason { get; set; }

    /// <summary>
    ///     Priority for response analysis (higher = process sooner)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    ///     Get response analysis context from HttpContext, or create default.
    /// </summary>
    public static ResponseAnalysisContext GetOrCreate(HttpContext context, string clientId)
    {
        if (context.Items.TryGetValue(HttpContextKey, out var obj) && obj is ResponseAnalysisContext existing)
            return existing;

        var newContext = new ResponseAnalysisContext
        {
            ClientId = clientId,
            EnableAnalysis = false
        };

        context.Items[HttpContextKey] = newContext;
        return newContext;
    }

    /// <summary>
    ///     Try to get existing context from HttpContext
    /// </summary>
    public static ResponseAnalysisContext? TryGet(HttpContext context)
    {
        return context.Items.TryGetValue(HttpContextKey, out var obj) && obj is ResponseAnalysisContext ctx
            ? ctx
            : null;
    }
}

/// <summary>
///     Thoroughness level for response analysis.
///     Determined by request-side risk signals.
/// </summary>
public enum ResponseAnalysisThoroughness
{
    /// <summary>Minimal analysis (status code only, fast)</summary>
    Minimal,

    /// <summary>Standard analysis (status + basic patterns)</summary>
    Standard,

    /// <summary>Thorough analysis (all detectors, full pattern matching)</summary>
    Thorough,

    /// <summary>Deep analysis (streaming, semantic content analysis, LLM if enabled)</summary>
    Deep
}

/// <summary>
///     Request-side trigger that can activate response analysis.
///     Implemented by detectors that run EARLY in the request pipeline.
/// </summary>
public interface IResponseAnalysisTrigger
{
    /// <summary>
    ///     Check if this detector's signals should trigger response analysis.
    ///     Called during request detection, BEFORE response is generated.
    /// </summary>
    /// <param name="state">Current blackboard state</param>
    /// <param name="context">Response analysis context to configure</param>
    /// <returns>True if response analysis should be triggered</returns>
    bool ShouldTriggerResponseAnalysis(
        BlackboardState state,
        ResponseAnalysisContext context);
}

/// <summary>
///     Signal emitted by request-side detector to configure response analysis.
/// </summary>
public sealed record ResponseAnalysisTriggerSignal
{
    /// <summary>
    ///     HttpContext.Items key for collecting all trigger signals
    /// </summary>
    public const string HttpContextKey = "BotDetection.ResponseAnalysisTriggerSignals";

    /// <summary>
    ///     Detector that emitted this trigger
    /// </summary>
    public required string DetectorName { get; init; }

    /// <summary>
    ///     Reason for triggering
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    ///     Requested thoroughness level
    /// </summary>
    public ResponseAnalysisThoroughness RequestedThoroughness { get; init; } = ResponseAnalysisThoroughness.Standard;

    /// <summary>
    ///     Requested mode (async vs inline)
    /// </summary>
    public ResponseAnalysisMode RequestedMode { get; init; } = ResponseAnalysisMode.Async;

    /// <summary>
    ///     Enable streaming analysis?
    /// </summary>
    public bool EnableStreaming { get; init; }

    /// <summary>
    ///     Priority (higher = more urgent)
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    ///     Additional signals to pass to response coordinator
    /// </summary>
    public IReadOnlyDictionary<string, object> Signals { get; init; } = new Dictionary<string, object>();

    /// <summary>
    ///     Add this trigger signal to HttpContext
    /// </summary>
    public void AddToContext(HttpContext context)
    {
        if (!context.Items.TryGetValue(HttpContextKey, out var obj) ||
            obj is not List<ResponseAnalysisTriggerSignal> list)
        {
            list = new List<ResponseAnalysisTriggerSignal>();
            context.Items[HttpContextKey] = list;
        }

        list.Add(this);
    }

    /// <summary>
    ///     Get all trigger signals from HttpContext
    /// </summary>
    public static IReadOnlyList<ResponseAnalysisTriggerSignal> GetAll(HttpContext context)
    {
        return context.Items.TryGetValue(HttpContextKey, out var obj) && obj is List<ResponseAnalysisTriggerSignal> list
            ? list
            : Array.Empty<ResponseAnalysisTriggerSignal>();
    }
}

/// <summary>
///     Helper for detectors to easily trigger response analysis
/// </summary>
public static class ResponseAnalysisTriggerHelper
{
    /// <summary>
    ///     Trigger response analysis from a detector.
    ///     Call this during ContributeAsync() to configure response analysis.
    /// </summary>
    public static void TriggerResponseAnalysis(
        this BlackboardState state,
        string detectorName,
        string reason,
        ResponseAnalysisThoroughness thoroughness = ResponseAnalysisThoroughness.Standard,
        ResponseAnalysisMode mode = ResponseAnalysisMode.Async,
        bool enableStreaming = false,
        int priority = 0,
        Dictionary<string, object>? additionalSignals = null)
    {
        // Create trigger signal
        var triggerSignal = new ResponseAnalysisTriggerSignal
        {
            DetectorName = detectorName,
            Reason = reason,
            RequestedThoroughness = thoroughness,
            RequestedMode = mode,
            EnableStreaming = enableStreaming,
            Priority = priority,
            Signals = additionalSignals ?? new Dictionary<string, object>()
        };

        // Add to context
        triggerSignal.AddToContext(state.HttpContext);

        // Get or create response analysis context
        var clientId = state.GetSignal<string>("client.id") ?? state.ClientIp ?? "unknown";
        var context = ResponseAnalysisContext.GetOrCreate(state.HttpContext, clientId);

        // Update context based on trigger
        context.EnableAnalysis = true;
        context.RequestBotProbability = state.CurrentRiskScore;
        context.TriggeringDetector = detectorName;
        context.TriggerReason = reason;

        // Upgrade thoroughness if requested higher
        if (thoroughness > context.Thoroughness) context.Thoroughness = thoroughness;

        // Upgrade to inline if requested
        if (mode == ResponseAnalysisMode.Inline && context.Mode == ResponseAnalysisMode.Async) context.Mode = mode;

        // Enable streaming if requested
        if (enableStreaming) context.EnableStreaming = true;

        // Update priority (take maximum)
        if (priority > context.Priority) context.Priority = priority;

        // Merge signals
        if (additionalSignals != null)
            foreach (var (key, value) in additionalSignals)
                context.TriggerSignals[key] = value;
    }

    /// <summary>
    ///     Quick trigger for honeypot hits (always deep analysis, inline, streaming)
    /// </summary>
    public static void TriggerHoneypotResponseAnalysis(
        this BlackboardState state,
        string detectorName,
        string honeypotPath)
    {
        state.TriggerResponseAnalysis(
            detectorName,
            $"Honeypot path accessed: {honeypotPath}",
            ResponseAnalysisThoroughness.Deep,
            ResponseAnalysisMode.Inline,
            true,
            100,
            new Dictionary<string, object>
            {
                ["honeypot.path"] = honeypotPath,
                ["honeypot.hit"] = true
            });
    }

    /// <summary>
    ///     Quick trigger for datacenter IPs (thorough analysis, async)
    /// </summary>
    public static void TriggerDatacenterResponseAnalysis(
        this BlackboardState state,
        string detectorName,
        string datacenterName)
    {
        state.TriggerResponseAnalysis(
            detectorName,
            $"Datacenter IP detected: {datacenterName}",
            ResponseAnalysisThoroughness.Thorough,
            ResponseAnalysisMode.Async,
            priority: 50,
            additionalSignals: new Dictionary<string, object>
            {
                ["ip.datacenter"] = datacenterName,
                ["ip.is_datacenter"] = true
            });
    }

    /// <summary>
    ///     Quick trigger for suspicious UA (standard analysis, async)
    /// </summary>
    public static void TriggerSuspiciousUaResponseAnalysis(
        this BlackboardState state,
        string detectorName,
        string reason)
    {
        state.TriggerResponseAnalysis(
            detectorName,
            reason,
            ResponseAnalysisThoroughness.Standard,
            ResponseAnalysisMode.Async,
            priority: 25);
    }

    /// <summary>
    ///     Quick trigger for high request-side risk (thorough analysis, inline if very high)
    /// </summary>
    public static void TriggerHighRiskResponseAnalysis(
        this BlackboardState state,
        string detectorName,
        double riskScore)
    {
        var mode = riskScore > 0.8
            ? ResponseAnalysisMode.Inline
            : ResponseAnalysisMode.Async;

        var thoroughness = riskScore switch
        {
            > 0.8 => ResponseAnalysisThoroughness.Deep,
            > 0.6 => ResponseAnalysisThoroughness.Thorough,
            _ => ResponseAnalysisThoroughness.Standard
        };

        state.TriggerResponseAnalysis(
            detectorName,
            $"High risk score: {riskScore:F2}",
            thoroughness,
            mode,
            riskScore > 0.8,
            (int)(riskScore * 100),
            new Dictionary<string, object>
            {
                ["risk.score"] = riskScore,
                ["risk.high"] = true
            });
    }
}