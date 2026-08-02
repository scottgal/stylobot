namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     2026-08-02 fp-cache-current architecture (final, operator-approved model): the
///     fingerprint's cached score updates in REAL TIME per observation, weighted by that
///     observation's EVIDENCE POWER. Supersedes <c>DriftReopenAbsorption</c>'s binary
///     background-window gate: "requests lack power" means WEAK evidence lacks power,
///     not that a request can never move the fingerprint. Two tiers:
///     <list type="number">
///         <item>Definitive (honeypot hit, verified-bad-bot, security-tool detection, high
///             threat/attack severity) -- sets the cached score DIRECTLY to the fresh
///             observation, one hit, no blend.</item>
///         <item>Graduated (everything else) -- EWMA-blends at an alpha derived from how
///             extreme and how well-backed this observation's own verdict is, floored at
///             the steady-state alpha (weak/ambiguous -> small nudge, must accumulate) and
///             capped below a full overwrite (very confident but non-categorical evidence
///             still doesn't erase history in one hit).</item>
///     </list>
/// </summary>
public static class EvidencePowerAbsorption
{
    /// <summary>
    ///     True when this observation is categorically definitive and should set the cached
    ///     score directly rather than blend. Grounded in the SAME evidence classification the
    ///     Tool-family demotion arm already uses
    ///     (<see cref="Orchestration.DetectionLedgerExtensions.HasHostileSignals"/>) plus a
    ///     verified-bad-bot early exit -- not a new invented metric.
    /// </summary>
    public static bool IsDefinitive(bool hasHostileSignals, bool earlyExitVerifiedBadBot) =>
        hasHostileSignals || earlyExitVerifiedBadBot;

    /// <summary>
    ///     Certainty in [0,1] that this observation's own verdict is trustworthy evidence,
    ///     derived from two fields already computed on every <c>AggregatedEvidence</c>: how
    ///     far the probability sits from the maximally-ambiguous midpoint, scaled by how much
    ///     confidence backs it. 0 for a coin-flip verdict or zero-confidence read; 1 only for
    ///     an extreme, fully-confident verdict.
    /// </summary>
    public static double ComputeCertainty(double botProbability, double confidence)
    {
        var extremity = Math.Abs(botProbability - 0.5) * 2.0;
        return Math.Clamp(extremity * confidence, 0.0, 1.0);
    }

    /// <summary>
    ///     Resolve the EWMA alpha for a graduated (non-definitive) observation: linear
    ///     interpolation from <paramref name="steadyStateAlpha"/> (certainty 0, weak evidence,
    ///     small nudge) to <paramref name="ceilingAlpha"/> (certainty 1, strong-but-not-
    ///     categorical evidence, moves the score hard without a full overwrite).
    /// </summary>
    public static double ResolveGraduatedAlpha(double certainty, double steadyStateAlpha, double ceilingAlpha)
    {
        var c = Math.Clamp(certainty, 0.0, 1.0);
        var alpha = steadyStateAlpha + (ceilingAlpha - steadyStateAlpha) * c;
        return Math.Clamp(alpha, 0.0, 1.0);
    }
}
