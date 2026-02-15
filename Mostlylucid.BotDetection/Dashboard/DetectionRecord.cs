using System.Collections.Immutable;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Dashboard;

/// <summary>
///     Comprehensive detection record for dashboard display.
///     Stores enough data to show detection timeline, detector contributions, and debugging info.
/// </summary>
public sealed record DetectionRecord
{
    /// <summary>Unique detection ID</summary>
    public required string DetectionId { get; init; }

    /// <summary>Detection timestamp (UTC)</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>Request path</summary>
    public required string Path { get; init; }

    /// <summary>HTTP method</summary>
    public required string Method { get; init; }

    /// <summary>Status code returned</summary>
    public required int StatusCode { get; init; }

    /// <summary>Response time in milliseconds</summary>
    public required int ResponseTimeMs { get; init; }

    // ===== Bot Detection Results =====

    /// <summary>Final bot probability (0.0-1.0)</summary>
    public required double BotProbability { get; init; }

    /// <summary>Confidence in the decision</summary>
    public required double Confidence { get; init; }

    /// <summary>Risk band assigned</summary>
    public required string RiskBand { get; init; }

    /// <summary>Is this a bot?</summary>
    public required bool IsBot { get; init; }

    /// <summary>Bot type (if detected)</summary>
    public string? BotType { get; init; }

    /// <summary>Bot name (if identified)</summary>
    public string? BotName { get; init; }

    /// <summary>Policy that was applied</summary>
    public string? PolicyName { get; init; }

    /// <summary>Action taken (allow, block, throttle, challenge, etc.)</summary>
    public string? Action { get; init; }

    // ===== Request Context (Optional - PII sensitive) =====

    /// <summary>Client IP address (if enabled)</summary>
    public string? ClientIp { get; init; }

    /// <summary>User agent string (if enabled)</summary>
    public string? UserAgent { get; init; }

    /// <summary>Geographic country code (if available)</summary>
    public string? CountryCode { get; init; }

    /// <summary>Accept-Language header (if enabled)</summary>
    public string? Locale { get; init; }

    /// <summary>Referer header (if enabled)</summary>
    public string? Referer { get; init; }

    // ===== Detector Contributions =====

    /// <summary>
    ///     Per-detector contributions showing what each detector found.
    ///     Key: detector name, Value: contribution details
    /// </summary>
    public required ImmutableDictionary<string, DetectorContribution> DetectorContributions { get; init; }

    /// <summary>Total number of detectors that ran</summary>
    public required int DetectorCount { get; init; }

    /// <summary>Total processing time across all detectors</summary>
    public required double TotalDetectorTimeMs { get; init; }

    /// <summary>Whether AI detectors (ONNX/LLM) contributed</summary>
    public required bool AiRan { get; init; }

    // ===== Signals (For Advanced Debugging) =====

    /// <summary>
    ///     Selected signals from the blackboard that are useful for debugging.
    ///     Stored as JSON dictionary. Only include non-PII signals.
    /// </summary>
    public ImmutableDictionary<string, object>? ImportantSignals { get; init; }

    /// <summary>Summary reasons (top 5)</summary>
    public required ImmutableList<string> TopReasons { get; init; }

    // ===== Metadata =====

    /// <summary>Server hostname (for distributed systems)</summary>
    public string? ServerHost { get; init; }

    /// <summary>YARP cluster (if proxied)</summary>
    public string? YarpCluster { get; init; }

    /// <summary>YARP destination (if proxied)</summary>
    public string? YarpDestination { get; init; }

    /// <summary>Was this detection escalated?</summary>
    public bool Escalated { get; init; }

    /// <summary>Detection pipeline version (for schema migration)</summary>
    public int SchemaVersion { get; init; } = 1;
}

/// <summary>
///     Individual detector's contribution to the final decision.
/// </summary>
public sealed record DetectorContribution
{
    /// <summary>Detector name</summary>
    public required string Name { get; init; }

    /// <summary>Category of evidence</summary>
    public required string Category { get; init; }

    /// <summary>Confidence delta contributed (+ve = bot, -ve = human)</summary>
    public required double ConfidenceDelta { get; init; }

    /// <summary>Weight applied to this detector</summary>
    public required double Weight { get; init; }

    /// <summary>Weighted contribution (delta * weight)</summary>
    public required double Contribution { get; init; }

    /// <summary>Primary reason from this detector</summary>
    public string? Reason { get; init; }

    /// <summary>Bot type suggested by this detector</summary>
    public string? SuggestedBotType { get; init; }

    /// <summary>Execution time in milliseconds</summary>
    public required double ExecutionTimeMs { get; init; }

    /// <summary>Priority/wave when this detector ran</summary>
    public required int Priority { get; init; }
}

/// <summary>
///     Factory for creating DetectionRecord from orchestrator results.
/// </summary>
public static class DetectionRecordFactory
{
    /// <summary>
    ///     Creates a detection record from aggregated evidence and HTTP context.
    ///     Respects privacy settings - only includes PII if explicitly enabled.
    /// </summary>
    public static DetectionRecord FromEvidence(
        AggregatedEvidence evidence,
        BlackboardState state,
        DetectionRecordOptions options)
    {
        return new DetectionRecord
        {
            DetectionId = state.RequestId,
            Timestamp = DateTime.UtcNow,
            Path = state.Path,
            Method = state.HttpContext.Request.Method,
            StatusCode = state.HttpContext.Response.StatusCode,
            ResponseTimeMs = (int)state.Elapsed.TotalMilliseconds,

            // Detection results
            BotProbability = evidence.BotProbability,
            Confidence = evidence.Confidence,
            RiskBand = evidence.RiskBand.ToString(),
            IsBot = evidence.BotProbability > 0.5,
            BotType = evidence.PrimaryBotType?.ToString(),
            BotName = evidence.PrimaryBotName,
            PolicyName = evidence.PolicyName,
            Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName,

            // Optional PII data
            ClientIp = options.IncludeClientIp ? state.ClientIp : null,
            UserAgent = options.IncludeUserAgent ? state.UserAgent : null,
            CountryCode = options.IncludeGeo ? state.GetSignal<string>("geo.country_code") : null,
            Locale = options.IncludeLocale ? state.HttpContext.Request.Headers.AcceptLanguage.ToString() : null,
            Referer = options.IncludeReferer ? state.HttpContext.Request.Headers.Referer.ToString() : null,

            // Detector contributions
            DetectorContributions = evidence.Contributions
                .GroupBy(c => c.DetectorName)
                .ToImmutableDictionary(
                    g => g.Key,
                    g => new DetectorContribution
                    {
                        Name = g.Key,
                        Category = g.First().Category,
                        ConfidenceDelta = g.Sum(c => c.ConfidenceDelta),
                        Weight = g.Sum(c => c.Weight),
                        Contribution = g.Sum(c => c.ConfidenceDelta * c.Weight),
                        Reason = string.Join("; ", g.Select(c => c.Reason).Where(r => !string.IsNullOrEmpty(r))),
                        SuggestedBotType = g.FirstOrDefault(c => !string.IsNullOrEmpty(c.BotType))?.BotType,
                        ExecutionTimeMs = g.Sum(c => c.ProcessingTimeMs),
                        Priority = g.First().Priority
                    }),

            DetectorCount = evidence.ContributingDetectors.Count,
            TotalDetectorTimeMs = evidence.TotalProcessingTimeMs,
            AiRan = evidence.AiRan,

            // Signals (filter for non-PII)
            ImportantSignals = options.IncludeSignals ? FilterImportantSignals(evidence.Signals) : null,

            // Top reasons
            TopReasons = evidence.Contributions
                .Where(c => !string.IsNullOrEmpty(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason!)
                .ToImmutableList(),

            // Metadata
            ServerHost = options.IncludeServerHost ? Environment.MachineName : null,
            YarpCluster = state.HttpContext.Items.TryGetValue("Yarp.Cluster", out var cluster)
                ? cluster?.ToString()
                : null,
            YarpDestination = state.HttpContext.Items.TryGetValue("Yarp.Destination", out var dest)
                ? dest?.ToString()
                : null,
            Escalated = state.HttpContext.Items.ContainsKey("BotDetection.Escalated"),
            SchemaVersion = 1
        };
    }

    private static ImmutableDictionary<string, object> FilterImportantSignals(
        IReadOnlyDictionary<string, object> signals)
    {
        // Only include signals that are safe for dashboard display (no PII)
        var allowedPrefixes = new[] { "ua.", "header.", "client.", "geo.", "ip.", "behavioral." };
        var blockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "client_ip", "ip_address", "email", "phone", "session_id", "cookie", "authorization"
        };

        return signals
            .Where(s => allowedPrefixes.Any(p => s.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        && !blockedKeys.Contains(s.Key))
            .Take(50) // Limit to 50 signals
            .ToImmutableDictionary();
    }
}

/// <summary>
///     Options controlling what data is included in detection records.
/// </summary>
public sealed record DetectionRecordOptions
{
    /// <summary>Include client IP address (PII)</summary>
    public bool IncludeClientIp { get; init; } = false;

    /// <summary>Include user agent string (fingerprinting data)</summary>
    public bool IncludeUserAgent { get; init; } = true;

    /// <summary>Include geographic data (country code)</summary>
    public bool IncludeGeo { get; init; } = true;

    /// <summary>Include locale/language data</summary>
    public bool IncludeLocale { get; init; } = true;

    /// <summary>Include referer header</summary>
    public bool IncludeReferer { get; init; } = false;

    /// <summary>Include blackboard signals</summary>
    public bool IncludeSignals { get; init; } = true;

    /// <summary>Include server hostname</summary>
    public bool IncludeServerHost { get; init; } = false;
}