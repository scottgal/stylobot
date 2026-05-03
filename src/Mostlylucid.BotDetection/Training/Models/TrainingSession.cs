using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Training.Models;

/// <summary>
///     Represents a training session with labeled bot/human traffic.
///     Serialized as JSONL (one session per line) for efficient streaming.
/// </summary>
public class TrainingSession
{
    /// <summary>Unique session identifier</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    /// <summary>Composite signature for this session</summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    /// <summary>Ground truth label: "bot" or "human"</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Confidence in the label (0.0-1.0)</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Session start time</summary>
    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    /// <summary>Session end time</summary>
    [JsonPropertyName("endTime")]
    public DateTime EndTime { get; set; }

    /// <summary>Extracted feature signatures</summary>
    [JsonPropertyName("features")]
    public SessionFeatures Features { get; set; } = new();

    /// <summary>Individual request observations</summary>
    [JsonPropertyName("observations")]
    public List<RequestObservation> Observations { get; set; } = new();

    /// <summary>Per-detector results from live detection</summary>
    [JsonPropertyName("detectorResults")]
    public Dictionary<string, DetectorResult> DetectorResults { get; set; } = new();

    /// <summary>Aggregated bot score from live detection</summary>
    [JsonPropertyName("aggregatedScore")]
    public double AggregatedScore { get; set; }

    /// <summary>Session-level metadata</summary>
    [JsonPropertyName("metadata")]
    public SessionMetadata Metadata { get; set; } = new();
}

/// <summary>
///     Extracted feature signatures for a session.
/// </summary>
public class SessionFeatures
{
    [JsonPropertyName("ipSignature")] public string IpSignature { get; set; } = "";

    [JsonPropertyName("uaSignature")] public string UaSignature { get; set; } = "";

    [JsonPropertyName("behaviorSignature")]
    public string BehaviorSignature { get; set; } = "";

    [JsonPropertyName("fingerprintHash")] public string FingerprintHash { get; set; } = "";

    [JsonPropertyName("custom")] public Dictionary<string, object> Custom { get; set; } = new();
}

/// <summary>
///     Individual request observation within a session.
/// </summary>
public class RequestObservation
{
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }

    [JsonPropertyName("path")] public string Path { get; set; } = "";

    [JsonPropertyName("method")] public string Method { get; set; } = "";

    [JsonPropertyName("statusCode")] public int StatusCode { get; set; }

    [JsonPropertyName("responseTime")] public int ResponseTime { get; set; }

    [JsonPropertyName("headers")] public Dictionary<string, string> Headers { get; set; } = new();

    [JsonPropertyName("cookies")] public List<string> Cookies { get; set; } = new();

    [JsonPropertyName("referrer")] public string? Referrer { get; set; }

    [JsonPropertyName("contentLength")] public int? ContentLength { get; set; }
}

/// <summary>
///     Detector result from live detection.
/// </summary>
public class DetectorResult
{
    [JsonPropertyName("confidence")] public double Confidence { get; set; }

    [JsonPropertyName("botType")] public string? BotType { get; set; }

    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

/// <summary>
///     Session-level metadata and statistics.
/// </summary>
public class SessionMetadata
{
    [JsonPropertyName("requestCount")] public int RequestCount { get; set; }

    [JsonPropertyName("avgRequestInterval")]
    public double AvgRequestInterval { get; set; }

    [JsonPropertyName("pathDiversity")] public double PathDiversity { get; set; }

    [JsonPropertyName("cookiePersistence")]
    public bool CookiePersistence { get; set; }

    [JsonPropertyName("referrerConsistency")]
    public bool ReferrerConsistency { get; set; }

    [JsonPropertyName("custom")] public Dictionary<string, object> Custom { get; set; } = new();
}