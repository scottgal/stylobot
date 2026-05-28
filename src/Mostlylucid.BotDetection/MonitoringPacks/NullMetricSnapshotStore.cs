namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     Sqlite-free no-op metric snapshot store. Periodic engine metrics
///     drop on the floor; commercial gateways register this so no Sqlite
///     file gets opened in a process that has no Sqlite layer. Reads
///     return empty time-series so the monitoring widgets render an
///     empty-state until a real Postgres-backed snapshot store lands.
/// </summary>
public sealed class NullMetricSnapshotStore : IMetricSnapshotStore
{
    public Task WriteSnapshotsAsync(IEnumerable<MetricSnapshot> snapshots, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<MetricSnapshot>> GetTimeSeriesAsync(
        string packId,
        string instrument,
        DateTime start,
        DateTime end,
        CancellationToken ct = default)
        => Task.FromResult(new List<MetricSnapshot>());

    public Task<List<MetricSnapshot>> GetLatestSnapshotsAsync(
        string packId,
        CancellationToken ct = default)
        => Task.FromResult(new List<MetricSnapshot>());

    public Task<int> PruneOldSnapshotsAsync(DateTime cutoff, CancellationToken ct = default)
        => Task.FromResult(0);
}
