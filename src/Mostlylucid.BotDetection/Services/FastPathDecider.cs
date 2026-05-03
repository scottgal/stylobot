using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Detection mode indicating how the request was processed.
/// </summary>
public enum DetectionMode
{
    /// <summary>Full 8-layer detection ran</summary>
    FullPath,

    /// <summary>Fast-path only (UA-based short-circuit)</summary>
    FastPath,

    /// <summary>Fast-path aborted early, sampled for full analysis</summary>
    FastPathSampled
}

/// <summary>
///     Result from the fast-path decider including detection mode and sampling info.
/// </summary>
public class FastPathDecision
{
    /// <summary>The detection result</summary>
    public required BotDetectionResult Result { get; init; }

    /// <summary>How the request was processed</summary>
    public DetectionMode Mode { get; init; }

    /// <summary>Whether this request was scheduled for full analysis in slow path</summary>
    public bool ScheduledForFullAnalysis { get; init; }

    /// <summary>The path that was requested</summary>
    public string? RequestPath { get; init; }

    /// <summary>Hash of the user-agent for pattern tracking</summary>
    public string? UaHash { get; init; }
}

/// <summary>
///     Decides whether to run fast-path (UA-only) or full detection.
///     Implements abort threshold, sampling, and always-full-path logic.
///     Key responsibilities:
///     - Short-circuit on high-confidence UA matches (abort threshold)
///     - Sample aborted requests for drift detection
///     - Force full path on critical endpoints
///     - Emit events to the learning bus for slow-path processing
/// </summary>
public class FastPathDecider
{
    private readonly IBotDetectionService _fullDetector;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<FastPathDecider> _logger;
    private readonly FastPathOptions _options;
    private readonly Random _random = new();
    private readonly IDetector _uaDetector;

    public FastPathDecider(
        ILogger<FastPathDecider> logger,
        IOptions<BotDetectionOptions> options,
        IDetector uaDetector,
        IBotDetectionService fullDetector,
        ILearningEventBus? learningBus = null)
    {
        _logger = logger;
        _options = options.Value.FastPath;
        _uaDetector = uaDetector;
        _fullDetector = fullDetector;
        _learningBus = learningBus;
    }

    /// <summary>
    ///     Decide and execute the appropriate detection path for this request.
    /// </summary>
    public async Task<FastPathDecision> DecideAndDetectAsync(
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        if (!_options.Enabled)
            // Fast path disabled - always run full detection
            return await RunFullPathAsync(httpContext, ct);

        var path = httpContext.Request.Path.Value ?? string.Empty;

        // Check if this path requires full detection
        if (IsAlwaysFullPath(path))
        {
            _logger.LogDebug("Path {Path} requires full detection", path);
            return await RunFullPathAsync(httpContext, ct);
        }

        // Run UA detection first (fastest detector)
        var uaResult = await _uaDetector.DetectAsync(httpContext, ct);
        var uaHash = ComputeUaHash(httpContext.Request.Headers.UserAgent.ToString());

        // Check for abort (high-confidence UA match)
        if (uaResult.Confidence >= _options.AbortThreshold)
            return await HandleAbortAsync(httpContext, uaResult, uaHash, path, ct);

        // UA not conclusive - run full detection
        return await RunFullPathAsync(httpContext, ct);
    }

    /// <summary>
    ///     Handle the abort case - UA is conclusive, but we may still sample for drift detection.
    /// </summary>
    private Task<FastPathDecision> HandleAbortAsync(
        HttpContext httpContext,
        DetectorResult uaResult,
        string uaHash,
        string path,
        CancellationToken ct)
    {
        var result = new BotDetectionResult
        {
            IsBot = uaResult.Confidence >= _options.AbortThreshold,
            ConfidenceScore = uaResult.Confidence,
            BotType = uaResult.BotType,
            BotName = uaResult.BotName,
            ProcessingTimeMs = 0 // Will be set by caller
        };
        result.Reasons.AddRange(uaResult.Reasons);

        var shouldSample = _random.NextDouble() < _options.SampleRate;

        // Always emit minimal detection event
        PublishMinimalDetection(httpContext, result, uaHash);

        if (shouldSample)
        {
            // Queue for full analysis in slow path
            PublishFullAnalysisRequest(httpContext, result, uaHash);

            _logger.LogDebug(
                "Fast-path abort sampled for full analysis: UA={UaHash}, confidence={Confidence:F2}",
                uaHash, uaResult.Confidence);

            return Task.FromResult(new FastPathDecision
            {
                Result = result,
                Mode = DetectionMode.FastPathSampled,
                ScheduledForFullAnalysis = true,
                RequestPath = path,
                UaHash = uaHash
            });
        }

        _logger.LogDebug(
            "Fast-path abort: UA={UaHash}, confidence={Confidence:F2}",
            uaHash, uaResult.Confidence);

        return Task.FromResult(new FastPathDecision
        {
            Result = result,
            Mode = DetectionMode.FastPath,
            ScheduledForFullAnalysis = false,
            RequestPath = path,
            UaHash = uaHash
        });
    }

    /// <summary>
    ///     Run full 8-layer detection.
    /// </summary>
    private async Task<FastPathDecision> RunFullPathAsync(
        HttpContext httpContext,
        CancellationToken ct)
    {
        var result = await _fullDetector.DetectAsync(httpContext, ct);
        var uaHash = ComputeUaHash(httpContext.Request.Headers.UserAgent.ToString());
        var path = httpContext.Request.Path.Value ?? string.Empty;

        // Emit full detection event
        PublishFullDetection(httpContext, result, uaHash);

        return new FastPathDecision
        {
            Result = result,
            Mode = DetectionMode.FullPath,
            ScheduledForFullAnalysis = false,
            RequestPath = path,
            UaHash = uaHash
        };
    }

    /// <summary>
    ///     Check if this path should always run full detection.
    /// </summary>
    private bool IsAlwaysFullPath(string path)
    {
        return _options.AlwaysRunFullOnPaths.Any(p =>
            path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Compute a hash of the user-agent for pattern tracking.
    /// </summary>
    private static string ComputeUaHash(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return "empty";

        // Simple hash for pattern grouping
        var hash = userAgent.GetHashCode();
        return $"ua_{hash:X8}";
    }

    /// <summary>
    ///     Publish minimal detection event (for every fast-path abort).
    /// </summary>
    private void PublishMinimalDetection(
        HttpContext httpContext,
        BotDetectionResult result,
        string uaHash)
    {
        _learningBus?.TryPublish(new LearningEvent
        {
            Type = LearningEventType.MinimalDetection,
            Source = nameof(FastPathDecider),
            Confidence = result.ConfidenceScore,
            Label = result.IsBot,
            Pattern = uaHash,
            RequestId = httpContext.TraceIdentifier,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = httpContext.Request.Headers.UserAgent.ToString(),
                ["ip"] = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["path"] = httpContext.Request.Path.Value ?? "",
                ["botType"] = result.BotType?.ToString() ?? "Unknown",
                ["botName"] = result.BotName ?? "",
                ["mode"] = "FastPath"
            }
        });
    }

    /// <summary>
    ///     Publish request for full analysis (sampled fast-path aborts).
    /// </summary>
    private void PublishFullAnalysisRequest(
        HttpContext httpContext,
        BotDetectionResult fastPathResult,
        string uaHash)
    {
        _learningBus?.TryPublish(new LearningEvent
        {
            Type = LearningEventType.FullAnalysisRequest,
            Source = nameof(FastPathDecider),
            Confidence = fastPathResult.ConfidenceScore,
            Label = fastPathResult.IsBot,
            Pattern = uaHash,
            RequestId = httpContext.TraceIdentifier,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = httpContext.Request.Headers.UserAgent.ToString(),
                ["ip"] = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["path"] = httpContext.Request.Path.Value ?? "",
                ["fastPathConfidence"] = fastPathResult.ConfidenceScore,
                ["fastPathIsBot"] = fastPathResult.IsBot,
                ["headers"] = ExtractHeadersForAnalysis(httpContext.Request.Headers),
                ["trigger"] = "sampling"
            }
        });
    }

    /// <summary>
    ///     Publish full detection result (when full path runs).
    /// </summary>
    private void PublishFullDetection(
        HttpContext httpContext,
        BotDetectionResult result,
        string uaHash)
    {
        _learningBus?.TryPublish(new LearningEvent
        {
            Type = LearningEventType.FullDetection,
            Source = nameof(FastPathDecider),
            Confidence = result.ConfidenceScore,
            Label = result.IsBot,
            Pattern = uaHash,
            RequestId = httpContext.TraceIdentifier,
            Metadata = new Dictionary<string, object>
            {
                ["userAgent"] = httpContext.Request.Headers.UserAgent.ToString(),
                ["ip"] = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["path"] = httpContext.Request.Path.Value ?? "",
                ["botType"] = result.BotType?.ToString() ?? "Unknown",
                ["botName"] = result.BotName ?? "",
                ["mode"] = "FullPath",
                ["processingTimeMs"] = result.ProcessingTimeMs,
                ["reasons"] = result.Reasons.ToList()
            }
        });
    }

    /// <summary>
    ///     Extract headers needed for offline full analysis.
    /// </summary>
    private static Dictionary<string, string> ExtractHeadersForAnalysis(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, string>();

        var relevantHeaders = new[]
        {
            "User-Agent", "Accept", "Accept-Language", "Accept-Encoding",
            "Referer", "Connection", "Sec-Fetch-Mode", "Sec-Fetch-Site",
            "Sec-Fetch-Dest", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform"
        };

        foreach (var header in relevantHeaders)
            if (headers.TryGetValue(header, out var value))
                result[header] = value.ToString();

        return result;
    }
}