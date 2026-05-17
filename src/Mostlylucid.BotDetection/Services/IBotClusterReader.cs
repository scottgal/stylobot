namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Read-only slice of <see cref="BotClusterService"/> consumed by the dashboard
///     (Clusters tab + cluster-diagnostics card). Extracted so a remote-mode dashboard
///     host can satisfy cluster reads over HTTP without dragging in the cluster
///     BackgroundService's full lifecycle (Leiden community detection, snapshot
///     rebuilds, LLM-label coordination).
///
///     <para>
///     Methods are async so the remote implementation can do HTTP I/O without
///     blocking thread-pool threads via <c>.GetAwaiter().GetResult()</c>. The local
///     implementation returns the in-memory snapshot via <c>Task.FromResult</c>.
///     </para>
/// </summary>
public interface IBotClusterReader
{
    /// <summary>The most-recently-computed cluster snapshot. Empty until first run.</summary>
    Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default);

    /// <summary>Diagnostics for the most-recent cluster run (timing, sizes, algorithm).</summary>
    Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default);
}
