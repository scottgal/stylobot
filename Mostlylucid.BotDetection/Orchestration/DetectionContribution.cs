using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;

// Use taxonomy types directly - no duplication
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

// Re-export taxonomy types for convenience
// BotDetection code can use these without changing imports
using DetectionContribution = Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution;
using CategoryScore = Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.CategoryScore;

/// <summary>
/// Verdict for early exit scenarios.
/// Domain-specific to bot detection.
/// </summary>
public enum EarlyExitVerdict
{
    /// <summary>Verified good bot (e.g., Googlebot with valid DNS)</summary>
    VerifiedGoodBot,

    /// <summary>Verified bad bot (e.g., known malicious signature)</summary>
    VerifiedBadBot,

    /// <summary>Whitelisted client (IP, UA, etc.)</summary>
    Whitelisted,

    /// <summary>Blacklisted client</summary>
    Blacklisted,

    /// <summary>Policy allowed early exit (fastpath confident decision)</summary>
    PolicyAllowed,

    /// <summary>Policy blocked early exit (fastpath confident decision)</summary>
    PolicyBlocked
}

/// <summary>
/// Aggregated result from detection.
/// This is the domain-specific view over DetectionLedger.
/// </summary>
public sealed record AggregatedEvidence
{
    /// <summary>
    /// The underlying detection ledger (source of truth).
    /// </summary>
    public DetectionLedger? Ledger { get; init; }

    /// <summary>
    /// Final aggregated bot probability (0.0 = human, 1.0 = bot).
    /// </summary>
    public required double BotProbability { get; init; }

    /// <summary>
    /// Confidence in the final decision (based on evidence strength).
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Risk band based on bot probability.
    /// </summary>
    public required RiskBand RiskBand { get; init; }

    /// <summary>
    /// Whether an early exit was triggered.
    /// </summary>
    public bool EarlyExit { get; init; }

    /// <summary>
    /// Early exit verdict if applicable.
    /// </summary>
    public EarlyExitVerdict? EarlyExitVerdict { get; init; }

    /// <summary>
    /// Primary bot type if identified.
    /// </summary>
    public BotType? PrimaryBotType { get; init; }

    /// <summary>
    /// Primary bot name if identified.
    /// </summary>
    public string? PrimaryBotName { get; init; }

    /// <summary>
    /// All signals collected from contributions.
    /// </summary>
    public IReadOnlyDictionary<string, object> Signals { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Total processing time in milliseconds.
    /// </summary>
    public double TotalProcessingTimeMs { get; init; }

    /// <summary>
    /// Breakdown by category for explainability.
    /// </summary>
    public IReadOnlyDictionary<string, CategoryScore> CategoryBreakdown { get; init; } =
        new Dictionary<string, CategoryScore>();

    /// <summary>
    /// Which detectors contributed.
    /// </summary>
    public IReadOnlySet<string> ContributingDetectors { get; init; } = new HashSet<string>();

    /// <summary>
    /// Which detectors failed or timed out.
    /// </summary>
    public IReadOnlySet<string> FailedDetectors { get; init; } = new HashSet<string>();

    /// <summary>
    /// Policy that was used for this detection.
    /// </summary>
    public string? PolicyName { get; init; }

    /// <summary>
    /// Action determined by policy (if any).
    /// </summary>
    public PolicyAction? PolicyAction { get; init; }

    /// <summary>
    /// Name of the action policy to execute (if specified by a transition).
    /// </summary>
    public string? TriggeredActionPolicyName { get; init; }

    /// <summary>
    /// Whether AI detectors (ONNX, LLM) contributed to this decision.
    /// </summary>
    public bool AiRan { get; init; }

    /// <summary>
    /// Unified threat score (0.0 = benign, 1.0 = malicious).
    /// Orthogonal to BotProbability — a human probing .env files has low BotProbability but high ThreatScore.
    /// </summary>
    public double ThreatScore { get; init; }

    /// <summary>
    /// Threat band classification based on ThreatScore.
    /// </summary>
    public ThreatBand ThreatBand { get; init; }

    /// <summary>
    /// All contributions (from ledger).
    /// </summary>
    public IReadOnlyList<DetectionContribution> Contributions =>
        Ledger?.Contributions ?? Array.Empty<DetectionContribution>();
}

/// <summary>
/// Risk bands for final classification.
/// Domain-specific to bot detection.
/// </summary>
public enum RiskBand
{
    /// <summary>Detection hasn't run or no data available</summary>
    Unknown = 0,

    /// <summary>Very low risk - likely human</summary>
    VeryLow = 1,

    /// <summary>Low risk - probably human</summary>
    Low = 2,

    /// <summary>Elevated risk - consider throttling or soft challenge</summary>
    Elevated = 3,

    /// <summary>Medium risk - uncertain, recommend challenge</summary>
    Medium = 4,

    /// <summary>High risk - probably bot</summary>
    High = 5,

    /// <summary>Very high risk - almost certainly bot</summary>
    VeryHigh = 6,

    /// <summary>Verified - confirmed by external verification (good or bad bot)</summary>
    Verified = 7
}

/// <summary>
/// Recommended actions based on risk assessment.
/// Domain-specific to bot detection.
/// </summary>
public enum RecommendedAction
{
    /// <summary>Allow the request normally</summary>
    Allow,

    /// <summary>Apply rate limiting or throttling</summary>
    Throttle,

    /// <summary>Present a challenge (CAPTCHA, proof-of-work, JS challenge)</summary>
    Challenge,

    /// <summary>Block the request</summary>
    Block
}

/// <summary>
/// Threat classification bands for intent scoring.
/// Orthogonal to RiskBand — measures malicious intent, not bot probability.
/// </summary>
public enum ThreatBand
{
    /// <summary>No threat detected (0.0 - 0.15)</summary>
    None,

    /// <summary>Low threat (0.15 - 0.35)</summary>
    Low,

    /// <summary>Elevated threat (0.35 - 0.55)</summary>
    Elevated,

    /// <summary>High threat (0.55 - 0.80)</summary>
    High,

    /// <summary>Critical threat (0.80 - 1.0)</summary>
    Critical
}
