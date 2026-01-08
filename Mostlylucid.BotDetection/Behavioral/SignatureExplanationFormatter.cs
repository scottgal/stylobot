using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Behavioral;

/// <summary>
///     Interface for formatting signature behavior into human-readable explanations.
/// </summary>
public interface ISignatureExplanationFormatter
{
    /// <summary>
    ///     Generates a human-readable explanation of signature behavior.
    /// </summary>
    /// <param name="state">Behavioral metrics</param>
    /// <param name="outcome">Detection outcome</param>
    /// <returns>Formatted explanation for dashboard display</returns>
    SignatureExplanation Explain(SignatureBehaviorState state, DetectionOutcome outcome);
}

/// <summary>
///     Formats signature behavior into plain English explanations for dashboards.
///     Uses the same thresholds as SignatureToBdfMapper so behavior → BDF
///     and behavior → explanation speak the same language.
/// </summary>
public sealed class SignatureExplanationFormatter : ISignatureExplanationFormatter
{
    private readonly SignatureToBdfMapperOptions _options;

    public SignatureExplanationFormatter(IOptions<SignatureToBdfMapperOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>
    ///     Generates a human-readable explanation of signature behavior.
    /// </summary>
    public SignatureExplanation Explain(SignatureBehaviorState state, DetectionOutcome outcome)
    {
        var highlights = GenerateHighlights(state, outcome);
        var summary = BuildSummary(state, outcome, highlights);
        var metrics = BuildMetrics(state, outcome);

        return new SignatureExplanation
        {
            Summary = summary,
            Highlights = highlights,
            RawMetrics = metrics
        };
    }

    /// <summary>
    ///     Generates bullet-point highlights of key behavioral features.
    /// </summary>
    private List<string> GenerateHighlights(SignatureBehaviorState state, DetectionOutcome outcome)
    {
        var highlights = new List<string>();

        // Timing patterns
        if (state.SpectralPeakToNoise >= _options.SpectralPnBot &&
            state.SpectralEntropy <= _options.SpectralEntropyBot)
            highlights.Add(
                "Requests are sent at highly regular intervals, typical of timer-driven scripts.");

        if (state.BurstScore >= _options.BurstScoreHigh)
            highlights.Add(
                "Traffic arrives in short, intense bursts rather than steady browsing sessions.");

        if (state.CoefficientOfVariation < _options.CoefficientOfVariationLow)
            highlights.Add(
                "Request timing is very consistent (low variation), suggesting automated behavior.");

        if (state.CoefficientOfVariation > _options.CoefficientOfVariationHigh)
            highlights.Add(
                "Request timing is highly variable, consistent with human think-time and distractions.");

        // Navigation patterns
        if (state.PathEntropy >= _options.PathEntropyHigh &&
            state.NavAnomalyScore >= _options.NavAnomalyHigh)
            highlights.Add(
                "Navigation frequently jumps to unusual or non-UI paths, consistent with scanners or crawlers.");

        if (state.PathEntropy <= _options.PathEntropyLow &&
            state.NavAnomalyScore >= _options.NavAnomalyHigh)
            highlights.Add(
                "URLs are accessed in sequential/deterministic patterns, typical of scraping scripts.");

        if (state.AffordanceFollowThroughRatio >= _options.AffordanceHigh &&
            state.NavAnomalyScore < _options.NavAnomalyHigh)
            highlights.Add(
                "Navigation mostly follows links exposed in the UI, consistent with real users clicking around.");

        if (state.AffordanceFollowThroughRatio <= _options.AffordanceLow)
            highlights.Add(
                "Most requests access URLs directly without following UI links, suggesting programmatic access.");

        // Error patterns
        if (state.FourOhFourRatio >= _options.FourOhFourRatioHigh)
            highlights.Add(
                "A large fraction of requests result in 404 errors, suggesting path probing or discovery.");

        if (state.FiveOhOhRatio >= _options.FiveOhOhRatioHigh)
            highlights.Add(
                "Significant number of 5xx server errors, possibly caused by aggressive request rates.");

        // Request rate
        if (state.AverageRps > 5.0)
            highlights.Add(
                $"High request rate ({state.AverageRps:F1} requests/sec) exceeds typical human browsing.");

        if (state.AverageRps < 0.2)
            highlights.Add(
                $"Very low request rate ({state.AverageRps:F2} requests/sec) suggests slow/passive monitoring.");

        // Positive indicators (if no negative highlights and low bot probability)
        if (!highlights.Any() && outcome.BotProbability < 0.3)
            highlights.Add(
                "Request timing, navigation and error patterns all fall within normal human ranges.");

        // Cap at reasonable number for display
        return highlights.Take(6).ToList();
    }

    /// <summary>
    ///     Builds a one-line summary of the signature's behavior.
    /// </summary>
    private string BuildSummary(
        SignatureBehaviorState state,
        DetectionOutcome outcome,
        IReadOnlyList<string> highlights)
    {
        // High-confidence bot
        if (outcome.BotProbability >= 0.9)
        {
            if (state.SpectralPeakToNoise >= _options.SpectralPnBot)
                return "This signature behaves like a high-confidence timer-driven bot.";

            if (state.BurstScore >= _options.BurstScoreHigh)
                return "This signature behaves like a high-confidence bursty scraper.";

            if (state.NavAnomalyScore >= _options.NavAnomalyHigh)
                return "This signature behaves like a high-confidence automated scanner.";

            return "This signature behaves like a high-confidence automated client.";
        }

        // High-confidence human
        if (outcome.BotProbability <= 0.2 && state.AffordanceFollowThroughRatio >= _options.AffordanceHigh)
            return "This signature shows natural, human-like browsing behaviour.";

        // Specific patterns
        if (state.BurstScore >= _options.BurstScoreHigh &&
            state.PathEntropy >= _options.PathEntropyHigh)
            return "This signature shows bursty, exploratory behaviour typical of scrapers.";

        if (state.PathEntropy <= _options.PathEntropyLow &&
            state.SpectralPeakToNoise >= _options.SpectralPnBot)
            return "This signature shows sequential, periodic behaviour typical of systematic crawlers.";

        if (state.NavAnomalyScore >= _options.NavAnomalyHigh &&
            state.FourOhFourRatio >= _options.FourOhFourRatioHigh)
            return "This signature shows path-discovery behaviour typical of security scanners.";

        // Mixed/ambiguous
        if (outcome.BotProbability >= 0.4 && outcome.BotProbability <= 0.6)
            return "This signature shows mixed behavioural characteristics; further review may be useful.";

        // Default
        return outcome.IsBot
            ? "This signature shows some bot-like characteristics but without strong conviction."
            : "This signature appears mostly human-like with some minor anomalies.";
    }

    /// <summary>
    ///     Builds raw metrics dictionary for UI display.
    /// </summary>
    private static Dictionary<string, object> BuildMetrics(
        SignatureBehaviorState state,
        DetectionOutcome outcome)
    {
        return new Dictionary<string, object>
        {
            ["BotProbability"] = outcome.BotProbability,
            ["RiskBand"] = outcome.RiskBand,
            ["PathEntropy"] = state.PathEntropy,
            ["TimingEntropy"] = state.TimingEntropy,
            ["CoefficientOfVariation"] = state.CoefficientOfVariation,
            ["BurstScore"] = state.BurstScore,
            ["SpectralPeakToNoise"] = state.SpectralPeakToNoise,
            ["SpectralEntropy"] = state.SpectralEntropy,
            ["NavAnomalyScore"] = state.NavAnomalyScore,
            ["AffordanceFollowThrough"] = state.AffordanceFollowThroughRatio,
            ["FourOhFourRatio"] = state.FourOhFourRatio,
            ["FiveOhOhRatio"] = state.FiveOhOhRatio,
            ["AverageRps"] = state.AverageRps,
            ["AverageSessionDuration"] = state.AverageSessionDurationSeconds,
            ["AverageRequestsPerSession"] = state.AverageRequestsPerSession
        };
    }
}