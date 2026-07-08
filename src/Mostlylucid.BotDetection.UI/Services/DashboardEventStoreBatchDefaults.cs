using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Backward-compatible default for <see cref="IDashboardEventStore.ComposeBatchAsync"/>:
///     fans out to the existing per-widget reads and assembles the bundle. Correct for every
///     backing (InMemory / SQLite / Postgres / Remote). Postgres overrides with a
///     single-scan version. Only the requested <see cref="DatasetKind"/>s are fetched.
/// </summary>
public static class DashboardEventStoreBatchDefaults
{
    public static async Task<DashboardDatasetBundle> FanOutAsync(
        IDashboardEventStore store, DashboardBatchRequest req, CancellationToken ct)
    {
        DashboardSummary? summary = null;
        IReadOnlyList<DashboardTimeSeriesPoint>? buckets = null;
        IReadOnlyList<DashboardTopBotEntry>? bots = null;
        IReadOnlyList<DashboardCountryStats>? geo = null;
        IReadOnlyList<DashboardEndpointStats>? endpoints = null;

        foreach (var d in req.Datasets)
        {
            ct.ThrowIfCancellationRequested();
            switch (d.Kind)
            {
                case DatasetKind.SummaryStats:
                    summary = await store.GetSummaryAsync(req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.TimeBuckets:
                    if (req.StartTime is null || req.EndTime is null)
                        throw new ArgumentException(
                            "ComposeBatchAsync: a TimeBuckets dataset requires both StartTime and EndTime (a time-series needs a window).",
                            nameof(req));
                    buckets = await store.GetTimeSeriesAsync(req.StartTime.Value, req.EndTime.Value, TimeSpan.FromMinutes(d.BucketMinutes), req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.BotAggregate:
                    bots = await store.GetTopBotsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.GeoBreakdown:
                    geo = await store.GetCountryStatsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.EndpointStats:
                    endpoints = await store.GetEndpointStatsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
            }
        }

        return new DashboardDatasetBundle(summary, buckets, bots, geo, endpoints);
    }
}