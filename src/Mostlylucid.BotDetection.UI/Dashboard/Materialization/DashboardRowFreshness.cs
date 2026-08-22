using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     The freshness class a dashboard row (and, by extension, the widget key that
///     represents it in a <see cref="DashboardPageManifest"/>) belongs to.
/// </summary>
public enum DashboardRowFreshnessClass
{
    /// <summary>
    ///     Broad traffic aggregates (Summary, Countries, Endpoints, UserAgents, Clusters).
    ///     Hour-stale is explicitly tolerated -- these are counts/rollups, not per-visitor
    ///     detection identity.
    /// </summary>
    Aggregate,

    /// <summary>
    ///     Detection output tied to a specific fingerprint/identity (Visitors, TopBots,
    ///     Sessions, Threats): bot names, scores, threat levels. Needs seconds-to-minutes
    ///     freshness -- this is the class that must never be under-served.
    /// </summary>
    Live,
}

/// <summary>
///     Maps a page manifest's declared widget keys to the freshness class(es) its bundle
///     touches. This is the generic mechanism THE NAMED INVARIANT (freshness floor /
///     min-cadence collapse) is built on: <see cref="DashboardRefreshCadence"/> takes the
///     MIN over <see cref="ClassesTouchedBy"/> rather than ever special-casing a
///     specific page key (e.g. "dashboard.traffic is always Live-class") -- so a future
///     page key that mixes widget keys from both classes gets the correct (faster)
///     effective cadence automatically, with zero new code here.
///     <para>
///         The widget-key -> class table below is the one small piece of explicit
///         knowledge Stage 2b adds: nothing in the existing manifest/composer machinery
///         (<see cref="DashboardPageManifest"/>, <c>DefaultDashboardPageComposer</c>,
///         <see cref="DashboardWidgetCatalog"/>) already records "which of the 9
///         dashboard rows is this widget key" as structured, freshness-relevant data --
///         the catalog only knows the FOSS-dataset/extension-kind a key needs, not how
///         stale it may get. The bundle-membership knowledge itself (which widget keys
///         belong to which page key) is fully reused from
///         <c>DefaultDashboardPageManifestSource</c> -- this map only adds the
///         class label per key, not a second copy of the bundling.
///     </para>
/// </summary>
public static class DashboardRowFreshness
{
    private static readonly IReadOnlyDictionary<string, DashboardRowFreshnessClass> WidgetKeyClasses =
        new Dictionary<string, DashboardRowFreshnessClass>(StringComparer.Ordinal)
        {
            // dashboard.traffic's Aggregate-tolerant widgets (hour-stale is fine).
            ["summary"] = DashboardRowFreshnessClass.Aggregate,
            ["time-chart"] = DashboardRowFreshnessClass.Aggregate,
            ["countries"] = DashboardRowFreshnessClass.Aggregate,
            ["endpoints"] = DashboardRowFreshnessClass.Aggregate,
            ["site-health"] = DashboardRowFreshnessClass.Aggregate,
            // UserAgents is fetched from SignatureAggregateCache today, not the content
            // cache (so it doesn't currently gate through this path at all) -- mapped here
            // anyway so a future widening that DOES route it through a manifest's widget
            // keys classifies correctly without touching this table.
            ["user-agents"] = DashboardRowFreshnessClass.Aggregate,

            // Clusters row: explicitly Aggregate per the approved design.
            [DashboardRowWidgetKeys.ClustersRaw] = DashboardRowFreshnessClass.Aggregate,

            // Live/sensitive: detection output (fingerprints/names/scores).
            // "top-bots" is dashboard.traffic's own BotAggregate widget -- it is ALSO the
            // data source StyloBotDashboardMiddleware's ResolveVisitorsRawAsync falls back
            // to for the Visitors row (composedPage?.BotAggregate), which is exactly what
            // forces dashboard.traffic's effective cadence to the Live floor: this one
            // widget key is the concrete reason the shared entry can never collapse onto
            // the slower Aggregate base.
            ["top-bots"] = DashboardRowFreshnessClass.Live,
            ["visitors"] = DashboardRowFreshnessClass.Live,
            // Live-tier overlays (render-once ruling, 2026-08-22): by definition need to be
            // fresher than the aggregate base -- explicit here rather than relying on the
            // unrecognized-key failsafe below, since "obviously live" deserves a named entry
            // like every other row, not a silent default.
            ["live-time-overlay"] = DashboardRowFreshnessClass.Live,
            ["live-endpoints-overlay"] = DashboardRowFreshnessClass.Live,
            [DashboardRowWidgetKeys.TopBotsRaw] = DashboardRowFreshnessClass.Live,
            [DashboardRowWidgetKeys.SessionsRaw] = DashboardRowFreshnessClass.Live,
            [DashboardRowWidgetKeys.ThreatsRaw] = DashboardRowFreshnessClass.Live,
        };

    /// <summary>
    ///     The freshness class a single widget key belongs to. An unrecognized key (a
    ///     future row this table hasn't been updated for) fails safe to
    ///     <see cref="DashboardRowFreshnessClass.Live"/> -- the FASTER class -- never
    ///     <see cref="DashboardRowFreshnessClass.Aggregate"/>: over-serving is fine and
    ///     expected, under-serving is the one thing that must never happen.
    /// </summary>
    public static DashboardRowFreshnessClass ClassOf(string widgetKey) =>
        WidgetKeyClasses.TryGetValue(widgetKey, out var cls) ? cls : DashboardRowFreshnessClass.Live;

    /// <summary>
    ///     The set of freshness classes touched by everything a manifest's bundle
    ///     currently serves. Empty only for a manifest with zero widget keys (shouldn't
    ///     occur in practice; <see cref="DashboardRefreshCadence"/> fails safe to the Live
    ///     base interval in that case too).
    /// </summary>
    public static IReadOnlySet<DashboardRowFreshnessClass> ClassesTouchedBy(DashboardPageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.WidgetKeys.Select(ClassOf).ToHashSet();
    }
}
