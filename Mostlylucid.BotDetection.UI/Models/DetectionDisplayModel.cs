namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for displaying bot detection results.
///     Works with both HttpContext.Items (inline middleware) and YARP proxy headers.
/// </summary>
public sealed class DetectionDisplayModel
{
    /// <summary>Is this request identified as a bot?</summary>
    public bool IsBot { get; init; }

    /// <summary>Bot probability (0.0 to 1.0)</summary>
    public double BotProbability { get; init; }

    /// <summary>Confidence in the decision (0.0 to 1.0)</summary>
    public double Confidence { get; init; }

    /// <summary>Risk band (VeryLow, Low, Medium, High, VeryHigh)</summary>
    public string RiskBand { get; init; } = "Unknown";

    /// <summary>Primary bot type if detected</summary>
    public string? BotType { get; init; }

    /// <summary>Bot name if identified</summary>
    public string? BotName { get; init; }

    /// <summary>Policy that was applied</summary>
    public string? PolicyName { get; init; }

    /// <summary>Action taken (Allow, Block, Throttle, Challenge)</summary>
    public string? Action { get; init; }

    /// <summary>Processing time in milliseconds</summary>
    public double ProcessingTimeMs { get; init; }

    /// <summary>Top reasons for the detection (up to 5)</summary>
    public List<string> TopReasons { get; set; } = new();

    /// <summary>Per-detector contributions</summary>
    public List<DetectorContributionDisplay> DetectorContributions { get; set; } = new();

    /// <summary>Request ID for correlation</summary>
    public string? RequestId { get; init; }

    /// <summary>Timestamp of detection</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>YARP cluster (if behind proxy)</summary>
    public string? YarpCluster { get; init; }

    /// <summary>YARP destination (if behind proxy)</summary>
    public string? YarpDestination { get; init; }

    /// <summary>Multi-factor signature information</summary>
    public MultiFactorSignatureDisplay? Signatures { get; init; }

    /// <summary>Was detection result available?</summary>
    public bool HasData => RequestId != null;
}

/// <summary>
///     Multi-factor signature display model with plain English explanations.
/// </summary>
public sealed class MultiFactorSignatureDisplay
{
    /// <summary>Primary signature (HMAC of IP+UA) - truncated for display</summary>
    public string? PrimarySignature { get; init; }

    /// <summary>IP signature (HMAC of IP) - truncated for display</summary>
    public string? IpSignature { get; init; }

    /// <summary>UA signature (HMAC of UA) - truncated for display</summary>
    public string? UaSignature { get; init; }

    /// <summary>Client-side fingerprint signature - truncated for display</summary>
    public string? ClientSideSignature { get; init; }

    /// <summary>Plugin signature - truncated for display</summary>
    public string? PluginSignature { get; init; }

    /// <summary>IP subnet signature (/24) - truncated for display</summary>
    public string? IpSubnetSignature { get; init; }

    /// <summary>Number of signature factors available</summary>
    public int FactorCount { get; init; }

    /// <summary>Plain English explanation of what these signatures mean</summary>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Which factors are available (for display)</summary>
    public List<string> AvailableFactors { get; init; } = new();

    /// <summary>When this signature was first observed (null if new)</summary>
    public DateTimeOffset? FirstSeen { get; init; }

    /// <summary>When this signature was last observed before this request</summary>
    public DateTimeOffset? LastSeen { get; init; }

    /// <summary>Total times this signature has been seen</summary>
    public int? TotalHits { get; init; }

    /// <summary>Whether this is a new (never seen before) signature</summary>
    public bool IsNew => FirstSeen == null;
}

/// <summary>
///     Individual detector's contribution to the final decision.
/// </summary>
public sealed class DetectorContributionDisplay
{
    /// <summary>Detector name</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Category of evidence</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Confidence delta contributed</summary>
    public double ConfidenceDelta { get; init; }

    /// <summary>Weight applied</summary>
    public double Weight { get; init; }

    /// <summary>Weighted contribution (delta * weight)</summary>
    public double Contribution { get; init; }

    /// <summary>Primary reason from this detector</summary>
    public string? Reason { get; init; }

    /// <summary>Execution time in milliseconds</summary>
    public double ExecutionTimeMs { get; init; }

    /// <summary>Priority/wave when this detector ran</summary>
    public int Priority { get; init; }
}