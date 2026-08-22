namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Declares which widget keys are present on a named dashboard page.
///     The composer resolves the set of <see cref="Models.DatasetKind"/>s required
///     by those widgets and issues a single <c>ComposeBatchAsync</c> call.
/// </summary>
public sealed record DashboardPageManifest(string PageKey, IReadOnlyList<string> WidgetKeys);

/// <summary>
///     Returns the <see cref="DashboardPageManifest"/> registered for a given page key,
///     or <c>null</c> when no manifest is registered for that key.
/// </summary>
public interface IDashboardPageManifestSource
{
    DashboardPageManifest? For(string pageKey);
}

/// <summary>
///     Pack hook to add widget key(s) to a page's manifest so a pack widget joins the
///     tick-warmed compose. Registered in DI; <see cref="DefaultDashboardPageManifestSource"/>
///     merges every contribution for a page into the manifest it returns. Composable —
///     no replace, no ordering race between packs (unlike calling <c>AddManifest</c>).
/// </summary>
public interface IDashboardManifestContribution
{
    /// <summary>The page whose manifest these keys extend (e.g. <c>"dashboard.traffic"</c>).</summary>
    string PageKey { get; }

    /// <summary>Widget keys to append (each maps to a <c>[DashboardWidget(key, …)]</c> attribute).</summary>
    IReadOnlyList<string> WidgetKeys { get; }
}

/// <summary>
///     Default (empty) manifest source. Task 3 seeds the traffic manifest via
///     <see cref="AddManifest"/>; commercial packs may add additional manifests
///     by resolving this from DI and calling the same method, or by registering
///     their own <see cref="IDashboardPageManifestSource"/> before
///     <c>AddStyloBotDashboard</c> (which uses <c>TryAddSingleton</c>).
/// </summary>
public class DefaultDashboardPageManifestSource : IDashboardPageManifestSource
{
    private readonly Dictionary<string, DashboardPageManifest> _manifests =
        new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IDashboardManifestContribution> _contributions;

    public DefaultDashboardPageManifestSource(
        IEnumerable<IDashboardManifestContribution>? contributions = null)
    {
        _contributions = contributions?.ToList()
            ?? (IReadOnlyList<IDashboardManifestContribution>)Array.Empty<IDashboardManifestContribution>();
        Seed(_manifests);
    }

    /// <summary>Override in a subclass to seed manifests at construction time.</summary>
    protected virtual void Seed(Dictionary<string, DashboardPageManifest> manifests)
    {
        // Traffic page: the current-window datasets the TrafficController composes in one batch,
        // plus site-health (degradation history) so the SbSiteHealth VC reads warm instead of
        // self-fetching /api/v1/site-health/history. Widget keys match the [DashboardWidget(key,...)]
        // attributes on the VCs. DashboardRowWidgetKeys.UserAgentsRaw rides alongside (not
        // [DashboardWidget]-backed -- see that constant's doc comment) so UserAgents shares
        // the SAME window/audience/domain scope as Summary/Countries/Endpoints instead of the
        // shell model reading the fixed-window DashboardAggregateCache snapshot directly.
        manifests["dashboard.traffic"] = new DashboardPageManifest(
            "dashboard.traffic",
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health", DashboardRowWidgetKeys.UserAgentsRaw,
                     "live-time-overlay", "live-endpoints-overlay" });

        manifests["dashboard.site"] = new DashboardPageManifest(
            "dashboard.site",
            // time-chart included so the prewarmed bundle carries TimeBuckets: the site
            // page's hits chart (SiteHealthViewComponent's analytics series) must read the
            // stash, not cold-fetch — the 2026-08-13 blank-graph defect baked an empty
            // series ("M0 0") on first load because the manifest never composed it.
            new[] { "summary", "time-chart", "endpoints", "site-health" });

        // Stage 2a: one manifest per row that isn't already covered by the Traffic
        // bundle above. Each declares exactly the ONE synthetic widget key
        // DefaultDashboardPageComposer checks for (DashboardRowWidgetKeys) -- these
        // keys aren't DashboardWidgetCatalog/[DashboardWidget]-backed (no VC attribute
        // maps them to a DatasetKind), which is fine: the catalog gracefully returns
        // null for unknown keys, and the composer resolves them via
        // DashboardRowRawFetchers instead of ComposeBatchAsync.
        manifests["dashboard.clusters"] = new DashboardPageManifest(
            "dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        manifests["dashboard.topbots"] = new DashboardPageManifest(
            "dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });
        manifests["dashboard.sessions"] = new DashboardPageManifest(
            "dashboard.sessions", new[] { DashboardRowWidgetKeys.SessionsRaw });
        manifests["dashboard.threats"] = new DashboardPageManifest(
            "dashboard.threats", new[] { DashboardRowWidgetKeys.ThreatsRaw });
    }

    /// <summary>Register or replace a manifest at runtime (e.g. from Task 3 wiring).</summary>
    public void AddManifest(DashboardPageManifest manifest) =>
        _manifests[manifest.PageKey] = manifest;

    public DashboardPageManifest? For(string pageKey)
    {
        var baseManifest = _manifests.TryGetValue(pageKey, out var m) ? m : null;

        // Merge pack-contributed widget keys for this page (deduped, statics first).
        var extra = _contributions
            .Where(c => string.Equals(c.PageKey, pageKey, StringComparison.Ordinal))
            .SelectMany(c => c.WidgetKeys)
            .ToList();
        if (extra.Count == 0) return baseManifest;

        var keys = new List<string>(baseManifest?.WidgetKeys ?? Array.Empty<string>());
        foreach (var k in extra)
            if (!keys.Contains(k, StringComparer.Ordinal))
                keys.Add(k);

        return new DashboardPageManifest(pageKey, keys);
    }
}
