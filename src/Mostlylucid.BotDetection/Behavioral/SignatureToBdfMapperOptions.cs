namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Configuration thresholds for mapping SignatureBehaviorState to BDF scenarios.
///     These thresholds determine how behavioral metrics are interpreted when
///     generating synthetic test scenarios from observed signatures.
///     NOTE: These are mapping heuristics, NOT detection thresholds.
/// </summary>
public sealed class SignatureToBdfMapperOptions
{
    /// <summary>
    ///     Path entropy threshold for "low" classification.
    ///     Below this suggests sequential/deterministic navigation.
    /// </summary>
    public double PathEntropyLow { get; init; } = 0.5;

    /// <summary>
    ///     Path entropy threshold for "high" classification.
    ///     Above this suggests exploratory/scanning navigation.
    /// </summary>
    public double PathEntropyHigh { get; init; } = 3.0;

    /// <summary>
    ///     Navigation anomaly threshold for "high" classification.
    ///     Above this suggests scanner or non-UI navigation.
    /// </summary>
    public double NavAnomalyHigh { get; init; } = 0.6;

    /// <summary>
    ///     Spectral peak-to-noise ratio threshold for bot classification.
    ///     Above this suggests timer-driven periodic behavior.
    /// </summary>
    public double SpectralPnBot { get; init; } = 4.0;

    /// <summary>
    ///     Spectral entropy threshold for bot classification.
    ///     Below this suggests simple periodic timing patterns.
    /// </summary>
    public double SpectralEntropyBot { get; init; } = 0.4;

    /// <summary>
    ///     Burst score threshold for "high" classification.
    ///     Above this suggests bursty/aggressive scraping.
    /// </summary>
    public double BurstScoreHigh { get; init; } = 0.7;

    /// <summary>
    ///     Affordance follow-through threshold for "low" classification.
    ///     Below this suggests non-UI navigation.
    /// </summary>
    public double AffordanceLow { get; init; } = 0.4;

    /// <summary>
    ///     Affordance follow-through threshold for "high" classification.
    ///     Above this suggests normal UI-driven browsing.
    /// </summary>
    public double AffordanceHigh { get; init; } = 0.8;

    /// <summary>
    ///     404 error ratio threshold for "high" classification.
    ///     Above this suggests path probing or discovery.
    /// </summary>
    public double FourOhFourRatioHigh { get; init; } = 0.3;

    /// <summary>
    ///     500 error ratio threshold for "high" classification.
    ///     Above this suggests aggressive behavior causing server issues.
    /// </summary>
    public double FiveOhOhRatioHigh { get; init; } = 0.2;

    /// <summary>
    ///     Timing entropy threshold for "low" classification.
    ///     Below this suggests consistent, timer-driven timing.
    /// </summary>
    public double TimingEntropyLow { get; init; } = 0.3;

    /// <summary>
    ///     Coefficient of variation threshold for "low" classification.
    ///     Below this suggests very consistent timing (bots).
    /// </summary>
    public double CoefficientOfVariationLow { get; init; } = 0.3;

    /// <summary>
    ///     Coefficient of variation threshold for "high" classification.
    ///     Above this suggests variable human-like timing.
    /// </summary>
    public double CoefficientOfVariationHigh { get; init; } = 0.7;
}