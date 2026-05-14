using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbMetricsTabViewComponent(
    IMetricSnapshotStore snapshotStore,
    StyloBotDashboardOptions options,
    IPackRuntimeController runtimeController)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string packId = "aspnet-monitoring")
    {
        var latest = await snapshotStore.GetLatestSnapshotsAsync(packId);
        return View(new MetricsTabModel
        {
            PackId = packId,
            LatestSnapshots = latest,
            IncludeHostMeters = options.MonitoringPack.IncludeAspNetHostMeters,
            BasePath = options.BasePath.TrimEnd('/'),
            SupportsHotReload = runtimeController.SupportsHotReload(packId)
        });
    }
}

public sealed class MetricsTabModel
{
    public required string PackId { get; init; }
    public required List<MetricSnapshot> LatestSnapshots { get; init; }
    public bool IncludeHostMeters { get; init; }
    public required string BasePath { get; init; }

    /// <summary>
    ///     True when a commercial pack has registered a hot-reload-capable
    ///     IPackRuntimeController. Drives whether the Razor partial renders the
    ///     edit-form HTMX slot. False under FOSS-only deployments.
    /// </summary>
    public bool SupportsHotReload { get; init; }

    public double GetLatest(string instrument, string valueType)
        => LatestSnapshots.FirstOrDefault(s => s.Instrument == instrument && s.ValueType == valueType)?.Value ?? 0;
}
