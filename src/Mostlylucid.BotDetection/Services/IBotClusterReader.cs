namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Read-only slice of <see cref="BotClusterService"/> consumed by the dashboard
///     (Clusters tab + cluster-diagnostics card). Extracted so a remote-mode dashboard
///     host can satisfy cluster reads over HTTP without dragging in the cluster
///     BackgroundService's full lifecycle (Leiden community detection, snapshot
///     rebuilds, LLM-label coordination).
/// </summary>
public interface IBotClusterReader
{
    /// <summary>The most-recently-computed cluster snapshot. Empty until first run.</summary>
    IReadOnlyList<BotCluster> GetClusters();

    /// <summary>Diagnostics for the most-recent cluster run (timing, sizes, algorithm).</summary>
    BotClusterService.ClusterDiagnosticsSnapshot GetDiagnostics();
}
