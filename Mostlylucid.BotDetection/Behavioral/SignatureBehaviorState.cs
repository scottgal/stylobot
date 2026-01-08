namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Behavioral waveform snapshot captured from a signature's activity over time.
///     This represents the measurable characteristics of how a client behaves across
///     multiple requests, used for:
///     - Behavioral analysis and anomaly detection
///     - Generating synthetic BDF scenarios
///     - Creating human-readable explanations for dashboards
///     - Training and validating behavioral detectors
/// </summary>
public sealed record SignatureBehaviorState
{
    /// <summary>
    ///     Path entropy - measures unpredictability of URL selection.
    ///     Low values (
    ///     < 0.5) suggest sequential/ deterministic behavior.
    ///         High values (>
    ///         3.0) suggest exploratory/scanning behavior.
    /// </summary>
    public double PathEntropy { get; init; }

    /// <summary>
    ///     Timing entropy - measures unpredictability of inter-request intervals.
    ///     Low values suggest timer-driven behavior (bots).
    ///     High values suggest human-like variability.
    /// </summary>
    public double TimingEntropy { get; init; }

    /// <summary>
    ///     Coefficient of Variation (CV) - stddev / mean of inter-request times.
    ///     Low values (
    ///     < 0.3) suggest consistent timing ( bots).
    ///         High values (>
    ///         0.7) suggest variable human timing.
    /// </summary>
    public double CoefficientOfVariation { get; init; }

    /// <summary>
    ///     Burst score - measures clustering of requests in time.
    ///     High values (> 0.7) suggest bursty/aggressive scraping.
    ///     Low values suggest steady browsing.
    /// </summary>
    public double BurstScore { get; init; }

    /// <summary>
    ///     Navigation anomaly score - measures how often URLs are non-afforded.
    ///     High values (> 0.6) suggest scanner/crawler behavior.
    ///     Low values suggest normal UI navigation.
    /// </summary>
    public double NavAnomalyScore { get; init; }

    /// <summary>
    ///     Spectral peak-to-noise ratio - strength of timing periodicity.
    ///     High values (> 4.0) suggest timer-driven scripts.
    ///     Low values suggest irregular human timing.
    /// </summary>
    public double SpectralPeakToNoise { get; init; }

    /// <summary>
    ///     Spectral entropy - complexity of timing frequency spectrum.
    ///     Low values (
    ///     < 0.4) suggest simple periodic patterns ( bots).
    ///         High values suggest complex human timing.
    /// </summary>
    public double SpectralEntropy { get; init; }

    /// <summary>
    ///     Affordance follow-through ratio - fraction of requests following UI links.
    ///     High values (> 0.8) suggest real users clicking UI elements.
    ///     Low values (< 0.4) suggest direct URL access or scanning.
    /// </summary>
    public double AffordanceFollowThroughRatio { get; init; }

    /// <summary>
    ///     404 error ratio - fraction of requests returning 404 Not Found.
    ///     High values (> 0.3) suggest path probing or discovery.
    ///     Low values suggest knowledge of valid paths.
    /// </summary>
    public double FourOhFourRatio { get; init; }

    /// <summary>
    ///     500 error ratio - fraction of requests returning 5xx server errors.
    ///     High values may suggest aggressive scraping causing server issues.
    /// </summary>
    public double FiveOhOhRatio { get; init; }

    /// <summary>
    ///     Average requests per second across all sessions.
    /// </summary>
    public double AverageRps { get; init; }

    /// <summary>
    ///     Average session duration in seconds.
    /// </summary>
    public double AverageSessionDurationSeconds { get; init; }

    /// <summary>
    ///     Average number of requests per session.
    /// </summary>
    public int AverageRequestsPerSession { get; init; }
}

/// <summary>
///     Behavioral profile classification for signature expectations.
/// </summary>
public enum SignatureBehaviorProfile
{
    /// <summary>Unknown or unclassified behavior</summary>
    Unknown,

    /// <summary>Expected to be legitimate human behavior</summary>
    ExpectedHuman,

    /// <summary>Expected to be bot/automated behavior</summary>
    ExpectedBot,

    /// <summary>Expected to show mixed human and bot characteristics</summary>
    ExpectedMixed
}

/// <summary>
///     Detection outcome for a signature or request.
/// </summary>
public sealed record DetectionOutcome
{
    /// <summary>Is this classified as a bot?</summary>
    public bool IsBot { get; init; }

    /// <summary>Bot probability (0.0-1.0)</summary>
    public double BotProbability { get; init; }

    /// <summary>Risk band classification</summary>
    public string RiskBand { get; init; } = "Unknown";
}

/// <summary>
///     Human-readable explanation of signature behavior for dashboards.
/// </summary>
public sealed record SignatureExplanation
{
    /// <summary>One-line summary of behavior</summary>
    public required string Summary { get; init; }

    /// <summary>Bullet-point highlights of key behavioral features</summary>
    public required IReadOnlyList<string> Highlights { get; init; }

    /// <summary>Raw metrics for UI display (optional)</summary>
    public IReadOnlyDictionary<string, object>? RawMetrics { get; init; }
}