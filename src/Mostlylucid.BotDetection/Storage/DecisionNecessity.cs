namespace Mostlylucid.BotDetection.Storage;

/// <summary>
///     Retention value for a bounded, self-optimizing store: <b>"how likely is a
///     future decision to hinge on this entry?"</b> A store keeps its
///     highest-value entries under pressure and sheds the resolved-and-harmless
///     first — value-of-information / active-learning sampling rather than pure
///     LFU or raw bot-score.
///     <para>
///     <c>value = (uncertainty ⊕ risk) × recency</c>, where:
///     </para>
///     <list type="bullet">
///       <item><b>uncertainty</b> — closeness to the <i>decision threshold</i>
///         (Gaussian, peaks AT the threshold). A borderline classification is
///         where the decision is live and could still flip: highest information
///         value. NOT raw bot-score — a confident p=0.98 is <i>certain</i>, so its
///         uncertainty is low.</item>
///       <item><b>risk</b> — threat score. A decision is consequential even once
///         classified, so high threat is retained regardless of certainty.</item>
///       <item><b>⊕</b> — noisy-OR: high if <i>either</i> uncertainty or risk is
///         high.</item>
///       <item><b>recency</b> — exponential time-decay; fresh entries outrank
///         stale ones.</item>
///     </list>
///     <para>
///     So: certain-and-harmless (far from the threshold, low threat) → value ≈ 0 →
///     evicted first; borderline OR high-threat, and recent → retained.
///     </para>
/// </summary>
public static class DecisionNecessity
{
    /// <summary>Default Gaussian half-width (in probability units) of the
    /// "decision is live" band around the threshold.</summary>
    public const double DefaultBandwidth = 0.15;

    /// <summary>
    ///     Closeness of <paramref name="botProbability"/> to the decision
    ///     <paramref name="threshold"/>, in (0,1]: 1 exactly at the threshold,
    ///     Gaussian falloff so confidence in <i>either</i> direction (certain
    ///     human or certain bot) tapers toward 0.
    /// </summary>
    public static double Uncertainty(double botProbability, double threshold, double bandwidth = DefaultBandwidth)
    {
        var z = (botProbability - threshold) / Math.Max(bandwidth, 1e-6);
        return Math.Exp(-z * z);
    }

    /// <summary>Exponential recency weight in (0,1]: 1 at age 0, halving every
    /// <paramref name="halfLifeSeconds"/>.</summary>
    public static double Recency(double ageSeconds, double halfLifeSeconds)
    {
        if (ageSeconds <= 0) return 1.0;
        if (halfLifeSeconds <= 0) return 0.0;
        return Math.Pow(2.0, -ageSeconds / halfLifeSeconds);
    }

    /// <summary>Combined retention value in [0,1]. Higher = keep, lower = shed.</summary>
    public static double Value(
        double botProbability,
        double threat,
        double ageSeconds,
        double threshold,
        double halfLifeSeconds,
        double bandwidth = DefaultBandwidth)
    {
        var u = Uncertainty(botProbability, threshold, bandwidth);
        var r = Math.Clamp(threat, 0.0, 1.0);
        var necessity = u + r - u * r;   // noisy-OR
        return necessity * Recency(ageSeconds, halfLifeSeconds);
    }

    /// <summary>
    ///     <see cref="Value"/> scaled to a monotone non-negative long for
    ///     <c>WriteBehindLfuStore.ColdnessScore</c> (lower = colder = evicted first).
    /// </summary>
    public static long ColdnessScore(
        double botProbability,
        double threat,
        double ageSeconds,
        double threshold,
        double halfLifeSeconds,
        double bandwidth = DefaultBandwidth)
        => (long)(Value(botProbability, threat, ageSeconds, threshold, halfLifeSeconds, bandwidth) * 1_000_000_000.0);
}
