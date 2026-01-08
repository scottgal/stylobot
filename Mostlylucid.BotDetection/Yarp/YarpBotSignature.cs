using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Yarp;

/// <summary>
///     Represents a comprehensive bot signature collected during YARP learning mode.
///     Contains ALL signals, detectors, and context for training purposes.
/// </summary>
public class YarpBotSignature
{
    /// <summary>Unique signature ID</summary>
    [JsonPropertyName("signatureId")]
    public string SignatureId { get; set; } = "";

    /// <summary>Timestamp when signature was captured</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>Request path</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>HTTP method (GET, POST, etc.)</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>Client IP address</summary>
    [JsonPropertyName("clientIp")]
    public string ClientIp { get; set; } = "";

    /// <summary>User-Agent string</summary>
    [JsonPropertyName("userAgent")]
    public string UserAgent { get; set; } = "";

    /// <summary>Bot detection result</summary>
    [JsonPropertyName("detection")]
    public YarpDetectionResult Detection { get; set; } = new();

    /// <summary>Per-detector results</summary>
    [JsonPropertyName("detectorOutputs")]
    public Dictionary<string, DetectorOutput> DetectorOutputs { get; set; } = new();

    /// <summary>Blackboard signals collected during detection</summary>
    [JsonPropertyName("signals")]
    public Dictionary<string, object> Signals { get; set; } = new();

    /// <summary>HTTP headers (if enabled)</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Cookies (if enabled)</summary>
    [JsonPropertyName("cookies")]
    public List<string>? Cookies { get; set; }

    /// <summary>Request body (if enabled)</summary>
    [JsonPropertyName("requestBody")]
    public string? RequestBody { get; set; }

    /// <summary>Response time in milliseconds</summary>
    [JsonPropertyName("responseTimeMs")]
    public int ResponseTimeMs { get; set; }

    /// <summary>Response status code</summary>
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    /// <summary>YARP cluster routed to (if applicable)</summary>
    [JsonPropertyName("cluster")]
    public string? Cluster { get; set; }

    /// <summary>Backend destination (if applicable)</summary>
    [JsonPropertyName("destination")]
    public string? Destination { get; set; }
}

/// <summary>
///     Bot detection result summary.
/// </summary>
public class YarpDetectionResult
{
    /// <summary>Is this request from a bot?</summary>
    [JsonPropertyName("isBot")]
    public bool IsBot { get; set; }

    /// <summary>Confidence score (0.0-1.0)</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Bot type (if detected)</summary>
    [JsonPropertyName("botType")]
    public string? BotType { get; set; }

    /// <summary>Bot name (if detected)</summary>
    [JsonPropertyName("botName")]
    public string? BotName { get; set; }

    /// <summary>Bot category (if detected)</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>Is search engine bot?</summary>
    [JsonPropertyName("isSearchEngine")]
    public bool IsSearchEngine { get; set; }

    /// <summary>Is malicious bot?</summary>
    [JsonPropertyName("isMalicious")]
    public bool IsMalicious { get; set; }

    /// <summary>Is social media bot?</summary>
    [JsonPropertyName("isSocialBot")]
    public bool IsSocialBot { get; set; }

    /// <summary>Detection reasons</summary>
    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; set; } = new();

    /// <summary>Policy used for detection</summary>
    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "";

    /// <summary>Action taken (allow, block, throttle, etc.)</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";
}

/// <summary>
///     Individual detector output.
/// </summary>
public class DetectorOutput
{
    /// <summary>Detector name</summary>
    [JsonPropertyName("detector")]
    public string Detector { get; set; } = "";

    /// <summary>Confidence contributed by this detector</summary>
    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    /// <summary>Weight applied to this detector</summary>
    [JsonPropertyName("weight")]
    public double Weight { get; set; }

    /// <summary>Weighted contribution (confidence * weight)</summary>
    [JsonPropertyName("contribution")]
    public double Contribution { get; set; }

    /// <summary>Detection reason from this detector</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>Bot type suggested by this detector</summary>
    [JsonPropertyName("suggestedBotType")]
    public string? SuggestedBotType { get; set; }

    /// <summary>Execution time in milliseconds</summary>
    [JsonPropertyName("executionTimeMs")]
    public double ExecutionTimeMs { get; set; }

    /// <summary>Wave number when this detector ran</summary>
    [JsonPropertyName("wave")]
    public int Wave { get; set; }
}