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

    public DefaultDashboardPageManifestSource()
    {
        Seed(_manifests);
    }

    /// <summary>Override in a subclass to seed manifests at construction time.</summary>
    protected virtual void Seed(Dictionary<string, DashboardPageManifest> manifests)
    {
        // Traffic page: the current-window datasets the TrafficController composes in one batch,
        // plus site-health (degradation history) so the SbSiteHealth VC reads warm instead of
        // self-fetching /api/v1/site-health/history. Widget keys match the [DashboardWidget(key,...)]
        // attributes on the VCs.
        manifests["dashboard.traffic"] = new DashboardPageManifest(
            "dashboard.traffic",
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health" });
    }

    /// <summary>Register or replace a manifest at runtime (e.g. from Task 3 wiring).</summary>
    public void AddManifest(DashboardPageManifest manifest) =>
        _manifests[manifest.PageKey] = manifest;

    public DashboardPageManifest? For(string pageKey) =>
        _manifests.TryGetValue(pageKey, out var m) ? m : null;
}
