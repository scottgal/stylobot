using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbMetricsTabViewComponent(
    IMetricSnapshotStore snapshotStore,
    StyloBotDashboardOptions options)
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
            BasePath = options.BasePath.TrimEnd('/')
        });
    }
}

public sealed class MetricsTabModel
{
    public required string PackId { get; init; }
    public required List<MetricSnapshot> LatestSnapshots { get; init; }
    public bool IncludeHostMeters { get; init; }
    public required string BasePath { get; init; }

    public double GetLatest(string instrument, string valueType)
        => LatestSnapshots.FirstOrDefault(s => s.Instrument == instrument && s.ValueType == valueType)?.Value ?? 0;
}
