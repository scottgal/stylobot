using Mostlylucid.BotDetection.RateLimit;
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
        ct.ThrowIfCancellationRequested();

        // The requested slices are independent reads — each Get*Async opens its own
        // SqliteConnection and shares no mutable state or transaction, so they are
        // launched CONCURRENTLY and awaited together. This turns the fan-out cost from
        // sum-of-scans (the old serial `foreach await`) into max-of-scans, which is what
        // kept /api/v1/compose-batch under the client timeout. Argument validation for
        // window-requiring kinds happens up-front (before any task starts) so a bad
        // request still throws synchronously rather than after firing off other reads.
        Task<DashboardSummary>? summaryTask = null;
        Task<List<DashboardTimeSeriesPoint>>? bucketsTask = null;
        Task<List<DashboardTopBotEntry>>? botsTask = null;
        Task<List<DashboardCountryStats>>? geoTask = null;
        Task<List<DashboardEndpointStats>>? endpointsTask = null;
        Task<IReadOnlyList<DegradationSnapshot>>? degradationsTask = null;

        foreach (var d in req.Datasets)
        {
            switch (d.Kind)
            {
                case DatasetKind.SummaryStats:
                    summaryTask = store.GetSummaryAsync(req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.TimeBuckets:
                    if (req.StartTime is null || req.EndTime is null)
                        throw new ArgumentException(
                            "ComposeBatchAsync: a TimeBuckets dataset requires both StartTime and EndTime (a time-series needs a window).",
                            nameof(req));
                    bucketsTask = store.GetTimeSeriesAsync(req.StartTime.Value, req.EndTime.Value, TimeSpan.FromMinutes(d.BucketMinutes), req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.BotAggregate:
                    botsTask = store.GetTopBotsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.GeoBreakdown:
                    geoTask = store.GetCountryStatsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.EndpointStats:
                    endpointsTask = store.GetEndpointStatsAsync(d.TopN, req.StartTime, req.EndTime, req.AudienceFilter, req.Domains);
                    break;
                case DatasetKind.DegradationHistory:
                    if (req.StartTime is null || req.EndTime is null)
                        throw new ArgumentException(
                            "ComposeBatchAsync: a DegradationHistory dataset requires both StartTime and EndTime (a history needs a window).",
                            nameof(req));
                    degradationsTask = store.GetDegradationHistoryAsync(req.StartTime.Value, req.EndTime.Value, ct);
                    break;
            }
        }

        // Await all launched reads together. Task.WhenAll observes every task so no
        // faulted read is left unobserved; the first exception is rethrown here.
        var pending = new List<Task>(6);
        if (summaryTask is not null) pending.Add(summaryTask);
        if (bucketsTask is not null) pending.Add(bucketsTask);
        if (botsTask is not null) pending.Add(botsTask);
        if (geoTask is not null) pending.Add(geoTask);
        if (endpointsTask is not null) pending.Add(endpointsTask);
        if (degradationsTask is not null) pending.Add(degradationsTask);
        if (pending.Count > 0) await Task.WhenAll(pending);

        return new DashboardDatasetBundle(
            summaryTask is null ? null : summaryTask.Result,
            bucketsTask is null ? null : bucketsTask.Result,
            botsTask is null ? null : botsTask.Result,
            geoTask is null ? null : geoTask.Result,
            endpointsTask is null ? null : endpointsTask.Result,
            degradationsTask is null ? null : degradationsTask.Result);
    }
}