namespace Mostlylucid.BotDetection.MonitoringPacks;

public interface IMetricSnapshotStore
{
    Task WriteSnapshotsAsync(IEnumerable<MetricSnapshot> snapshots, CancellationToken ct = default);

    Task<List<MetricSnapshot>> GetTimeSeriesAsync(
        string packId,
        string instrument,
        DateTime start,
        DateTime end,
        CancellationToken ct = default);

    Task<List<MetricSnapshot>> GetLatestSnapshotsAsync(
        string packId,
        CancellationToken ct = default);

    Task<int> PruneOldSnapshotsAsync(DateTime cutoff, CancellationToken ct = default);
}
