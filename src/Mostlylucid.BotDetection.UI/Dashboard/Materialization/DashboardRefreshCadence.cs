using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Pure function computing a page key's effective refresh interval (seconds) for the
///     Stage 2b tick-driven due-time gate. No I/O, no state -- every input is a parameter,
///     so it is fully unit-testable independent of the coordinator, the content cache, or
///     wall-clock time.
///     <para>
///         Folds in, in order:
///     </para>
///     <list type="number">
///         <item>
///             <b>THE NAMED INVARIANT</b> ("freshness floor / min-cadence collapse"): the
///             base interval is the MIN over every <see cref="DashboardRowFreshnessClass"/>
///             the manifest's widget-key bundle touches (<see cref="DashboardRowFreshness.ClassesTouchedBy"/>).
///             A shared cache entry must never serve staler than any freshness class it
///             currently satisfies -- so this is always MIN, never an average, a max, or a
///             hardcoded per-page-key special case.
///         </item>
///         <item>
///             LFU-hotness scaling: a harmonic decay (<c>1/(1+accessCount)</c>) pulls a
///             frequently-read key's interval down toward the floor; a rarely-read one stays
///             at the (unscaled) class base interval. Parameter-free by construction --
///             no separate tunable normalization constant is needed for a curve that is
///             already monotonic in the right direction and naturally bounded.
///         </item>
///         <item>
///             The adaptive scale factor (from <see cref="DashboardMaterializerAdaptiveController"/>,
///             always &gt;= 1.0) is applied multiplicatively -- a uniform stretch under
///             measured-cost pressure, applied identically to every page key rather than
///             singling any one out.
///         </item>
///         <item>
///             <see cref="DashboardMaterializerOptions.GlobalMinIntervalSeconds"/> is the
///             final, unconditional floor: nothing above can push the result below it.
///         </item>
///     </list>
/// </summary>
public static class DashboardRefreshCadence
{
    /// <summary>
    ///     Computes the effective refresh interval (seconds) for one page key.
    /// </summary>
    /// <param name="manifest">The page's manifest (its widget keys determine the freshness classes touched).</param>
    /// <param name="accessCount">
    ///     The envelope's current LFU access count (from <c>SlidingCacheAtom.TryGetEntryStats</c>
    ///     via <c>IDashboardContentCache.LiveEnvelopes()</c>). 0 for an envelope with no
    ///     tracked hotness yet (e.g. a Tier-1 pinned prewarm window).
    /// </param>
    /// <param name="adaptiveScaleFactor">
    ///     The current uniform stretch factor from <see cref="DashboardMaterializerAdaptiveController"/>.
    ///     Values below 1.0 are clamped to 1.0 -- this function only ever slows a refresh
    ///     down relative to the class/hotness-derived value, never speeds it up.
    /// </param>
    /// <param name="options">The materializer options snapshot (startup-snapshot IOptions value).</param>
    public static int ComputeEffectiveIntervalSeconds(
        DashboardPageManifest manifest,
        int accessCount,
        double adaptiveScaleFactor,
        DashboardMaterializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);

        var floor = Math.Max(1, options.GlobalMinIntervalSeconds);

        // Step 1: THE NAMED INVARIANT -- MIN over every freshness class this page key's
        // bundle touches. An empty widget-key set (shouldn't occur) fails safe to the
        // Live base, not the Aggregate one.
        var classes = DashboardRowFreshness.ClassesTouchedBy(manifest);
        var baseInterval = classes.Count == 0
            ? options.LiveBaseIntervalSeconds
            : classes.Min(c => c == DashboardRowFreshnessClass.Aggregate
                ? options.AggregateBaseIntervalSeconds
                : options.LiveBaseIntervalSeconds);
        baseInterval = Math.Max(baseInterval, floor);

        // Step 2: LFU-hotness scaling. accessCount == 0 -> hotness == 1.0 -> the class base
        // interval, completely unscaled. As accessCount grows, hotness shrinks asymptotically
        // toward 0, pulling the interval down toward `floor` (never below it -- the harmonic
        // decay only ever narrows the gap, and the final clamp in Step 4 is what actually
        // enforces the floor exactly).
        var hotness = 1.0 / (1.0 + Math.Max(0, accessCount));
        var hotnessScaled = floor + (baseInterval - floor) * hotness;

        // Step 3: adaptive stretch. Never a speed-up: factors below 1.0 are clamped to 1.0.
        var scale = Math.Max(1.0, adaptiveScaleFactor);
        var scaled = hotnessScaled * scale;

        // Step 4: the global floor -- a hard ceiling on refresh RATE, regardless of anything above.
        return (int)Math.Max(floor, Math.Round(scaled, MidpointRounding.AwayFromZero));
    }
}
