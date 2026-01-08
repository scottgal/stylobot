namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Signal captured from an HTTP response for analysis.
///     This is PII-safe - contains no full response bodies, only patterns and metrics.
/// </summary>
public sealed record ResponseSignal
{
    /// <summary>
    ///     Unique request identifier (correlates with request detection)
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     Client identifier (IP hash + UA hash or fingerprint)
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    ///     When this response was sent
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    ///     HTTP status code (200, 404, 500, etc.)
    /// </summary>
    public required int StatusCode { get; init; }

    /// <summary>
    ///     Response size in bytes
    /// </summary>
    public required long ResponseBytes { get; init; }

    /// <summary>
    ///     Request path that generated this response
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    ///     HTTP method (GET, POST, etc.)
    /// </summary>
    public required string Method { get; init; }

    /// <summary>
    ///     Response headers (selected safe headers only)
    /// </summary>
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    ///     PII-safe summary of response body
    /// </summary>
    public required ResponseBodySummary BodySummary { get; init; }

    /// <summary>
    ///     Processing time in milliseconds
    /// </summary>
    public double ProcessingTimeMs { get; init; }

    /// <summary>
    ///     Bot probability from request-side detection (0.0-1.0)
    /// </summary>
    public double RequestBotProbability { get; init; }

    /// <summary>
    ///     Was response analysis requested inline (vs async)?
    /// </summary>
    public bool InlineAnalysis { get; init; }
}

/// <summary>
///     PII-safe summary of response body content.
///     Contains NO actual body text - only pattern matches and indicators.
/// </summary>
public sealed record ResponseBodySummary
{
    /// <summary>
    ///     Does response have a body?
    /// </summary>
    public required bool IsPresent { get; init; }

    /// <summary>
    ///     Body length in bytes
    /// </summary>
    public required int Length { get; init; }

    /// <summary>
    ///     Matched pattern names (NOT the actual content).
    ///     Example: ["stack_trace_marker", "rate_limit_message"]
    /// </summary>
    public IReadOnlyList<string> MatchedPatterns { get; init; }
        = Array.Empty<string>();

    /// <summary>
    ///     Template/page identifier if known (e.g., "login-failed-page", "404-error")
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    ///     Content-Type header value
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    ///     Hash of response body for duplicate detection (optional)
    /// </summary>
    public string? BodyHash { get; init; }
}

/// <summary>
///     Mode for response analysis triggers
/// </summary>
public enum ResponseAnalysisMode
{
    /// <summary>No response analysis</summary>
    None,

    /// <summary>Async analysis after response sent (default, zero request latency)</summary>
    Async,

    /// <summary>Inline analysis before response sent (adds latency, for critical paths)</summary>
    Inline
}

/// <summary>
///     Trigger configuration for when response analysis should activate.
///     Uses fast signature matching to decide if response coordinator should spin up.
/// </summary>
public sealed record ResponseAnalysisTrigger
{
    /// <summary>
    ///     Minimum request-side bot probability to trigger response analysis.
    ///     Default: 0.4 (medium confidence bots get response analysis)
    /// </summary>
    public double MinRequestBotProbability { get; init; } = 0.4;

    /// <summary>
    ///     Trigger response analysis for specific status code ranges
    /// </summary>
    public IReadOnlyList<StatusCodeRange> StatusCodeTriggers { get; init; }
        = new[] { new StatusCodeRange(400, 499), new StatusCodeRange(500, 599) };

    /// <summary>
    ///     Specific paths that always trigger response analysis (honeypots, admin paths, etc.)
    /// </summary>
    public IReadOnlyList<string> PathTriggers { get; init; }
        = Array.Empty<string>();

    /// <summary>
    ///     Client IDs (signature hashes) flagged for response analysis
    /// </summary>
    public IReadOnlySet<string> ClientIdTriggers { get; init; }
        = new HashSet<string>();

    /// <summary>
    ///     Analysis mode (async vs inline)
    /// </summary>
    public ResponseAnalysisMode Mode { get; init; } = ResponseAnalysisMode.Async;

    /// <summary>
    ///     Check if this response should trigger analysis
    /// </summary>
    public bool ShouldAnalyze(
        string clientId,
        string path,
        int statusCode,
        double requestBotProbability)
    {
        // Always analyze flagged clients
        if (ClientIdTriggers.Contains(clientId))
            return true;

        // Always analyze trigger paths
        if (PathTriggers.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Analyze if request-side confidence high enough
        if (requestBotProbability >= MinRequestBotProbability)
            return true;

        // Analyze if status code matches triggers
        if (StatusCodeTriggers.Any(range => range.Contains(statusCode)))
            return true;

        return false;
    }
}

/// <summary>
///     Status code range for matching
/// </summary>
public readonly record struct StatusCodeRange(int Min, int Max)
{
    public bool Contains(int statusCode)
    {
        return statusCode >= Min && statusCode <= Max;
    }

    public override string ToString()
    {
        return $"{Min}-{Max}";
    }
}