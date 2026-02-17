namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Result of bot detection analysis
/// </summary>
public class BotDetectionResult
{
    /// <summary>
    ///     Overall confidence score (0.0 to 1.0) that the request is from a bot
    /// </summary>
    public double ConfidenceScore { get; set; }

    /// <summary>
    ///     Whether this is classified as a bot (typically confidence > 0.7)
    /// </summary>
    public bool IsBot { get; set; }

    /// <summary>
    ///     List of reasons why the request was flagged as a bot
    /// </summary>
    public List<DetectionReason> Reasons { get; set; } = new();

    /// <summary>
    ///     Type of bot detected (if any)
    /// </summary>
    public BotType? BotType { get; set; }

    /// <summary>
    ///     Name of the identified bot (if known)
    /// </summary>
    public string? BotName { get; set; }

    /// <summary>
    ///     Processing time in milliseconds
    /// </summary>
    public long ProcessingTimeMs { get; set; }
}

/// <summary>
///     Individual detection reason
/// </summary>
public class DetectionReason
{
    /// <summary>
    ///     Category of detection
    /// </summary>
    public required string Category { get; set; }

    /// <summary>
    ///     Specific detail about what was detected
    /// </summary>
    public required string Detail { get; set; }

    /// <summary>
    ///     Contribution to overall confidence (0.0 to 1.0)
    /// </summary>
    public double ConfidenceImpact { get; set; }
}

/// <summary>
///     Types of bots
/// </summary>
public enum BotType
{
    Unknown,
    SearchEngine,
    SocialMediaBot,
    MonitoringBot,
    Scraper,
    MaliciousBot,
    GoodBot,
    VerifiedBot,
    AiBot,
    Tool
}