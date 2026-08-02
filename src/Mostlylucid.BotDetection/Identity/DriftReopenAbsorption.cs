namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Phase 1 of the 2026-08-02 fp-cache-current architecture: resolves which EWMA alpha
///     a <c>CachedBotProbability</c> write should use. Steady-state absorption is
///     deliberately slow (strong memory) so one-off noise doesn't move a fingerprint's
///     identity score -- but that same slowness meant a fingerprint whose behaviour
///     genuinely changed (drift detected by <see cref="FingerprintDriftService"/>) stayed
///     stuck showing its OLD score for dozens of requests while new, strong, contradicting
///     evidence piled up. While a fingerprint is inside its drift-reopen window, writes use
///     a much wider alpha (<c>IdentityDriftOptions.DriftReopenAlpha</c>) so the cache
///     catches up within ~1-2 observations instead.
///     <para>
///     Takes the steady-state / reopen alphas as plain doubles rather than an options
///     object: the codebase has two independent EWMA-alpha knobs today
///     (<c>IdentityEngineOptions.VerdictEwmaAlpha</c>, which <c>SqliteFingerprintStore</c>'s
///     verdict-write path actually uses, and <c>IdentityDriftOptions.CachedScoreEwmaAlpha</c>,
///     a second, currently-unused-by-that-path knob of the same name/intent) -- this
///     resolver stays agnostic to which one a caller wires as "steady state" rather than
///     silently picking one and changing the other's behaviour as a side effect.
///     </para>
/// </summary>
public static class DriftReopenAbsorption
{
    /// <summary>
    ///     Resolve the EWMA alpha for a verdict write. <paramref name="driftReopenedUntilUtc"/>
    ///     is the fingerprint's own <c>DriftReopenedUntilUtc</c> -- null, or a timestamp that
    ///     has already passed, resolves to <paramref name="steadyStateAlpha"/>.
    /// </summary>
    public static double ResolveAlpha(
        DateTime? driftReopenedUntilUtc,
        DateTime nowUtc,
        double steadyStateAlpha,
        double reopenAlpha)
    {
        var reopened = driftReopenedUntilUtc.HasValue && nowUtc < driftReopenedUntilUtc.Value;
        var alpha = reopened ? reopenAlpha : steadyStateAlpha;
        return Math.Clamp(alpha, 0.0, 1.0);
    }
}
