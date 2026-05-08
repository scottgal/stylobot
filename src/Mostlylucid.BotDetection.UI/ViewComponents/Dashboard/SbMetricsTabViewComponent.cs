using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbMetricsTabViewComponent(
    IMetricSnapshotStore snapshotStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string packId = "aspnet-monitoring")
    {
        var latest = await snapshotStore.GetLatestSnapshotsAsync(packId);
        return View(new MetricsTabModel
        {
            PackId = packId,
            LatestSnapshots = latest,
            IncludeHostMeters = options.Value.MonitoringPack.IncludeAspNetHostMeters,
            BasePath = options.Value.BasePath.TrimEnd('/')
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
