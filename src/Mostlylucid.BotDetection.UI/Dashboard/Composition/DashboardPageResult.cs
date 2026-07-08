using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     The resolved per-page dataset bundle, produced by <see cref="IDashboardPageComposer"/>
///     and stashed in <c>HttpContext.Items["sb.dashboard.pageresult"]</c>.
///     ViewComponents read their slice from this result when present; otherwise they
///     fall back to their existing self-fetch (backward-compatible path).
/// </summary>
public sealed class DashboardPageResult
{
    private readonly DashboardDatasetBundle _bundle;

    public DashboardPageResult(DashboardDatasetBundle bundle)
    {
        _bundle = bundle;
    }

    /// <summary>Summary statistics slice; null when <see cref="DatasetKind.SummaryStats"/> was not requested.</summary>
    public DashboardSummary? Summary => _bundle.Summary;

    /// <summary>Time-series buckets slice; null when <see cref="DatasetKind.TimeBuckets"/> was not requested.</summary>
    public IReadOnlyList<DashboardTimeSeriesPoint>? TimeBuckets => _bundle.TimeBuckets;

    /// <summary>Top-bot aggregate slice; null when <see cref="DatasetKind.BotAggregate"/> was not requested.</summary>
    public IReadOnlyList<DashboardTopBotEntry>? BotAggregate => _bundle.BotAggregate;

    /// <summary>Geo breakdown slice; null when <see cref="DatasetKind.GeoBreakdown"/> was not requested.</summary>
    public IReadOnlyList<DashboardCountryStats>? Geo => _bundle.Geo;

    /// <summary>Endpoint stats slice; null when <see cref="DatasetKind.EndpointStats"/> was not requested.</summary>
    public IReadOnlyList<DashboardEndpointStats>? Endpoints => _bundle.Endpoints;
}
