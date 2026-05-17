using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only cluster surface that proxies to the gateway's <c>/api/v1/clusters</c>.
///     The gateway's snapshot is authoritative; this adapter holds no state of its own.
///     Calls are synchronous-looking against an async HTTP call - the dashboard's Clusters
///     tab does these on render, so the operator sees a tiny gateway round-trip rather
///     than stale local data. Cache rolls into <see cref="GatewayApiClient"/>'s HttpClient
///     if needed (we don't enable HTTP caching here by default).
/// </summary>
internal sealed class RemoteBotClusterReader : IBotClusterReader
{
    private readonly GatewayApiClient _api;
    // Cached on first read so subsequent calls in the same render don't refetch. Cleared
    // by the SignalR invalidation relay when the gateway broadcasts a cluster-change beacon.
    private volatile IReadOnlyList<BotCluster>? _clusters;
    private volatile BotClusterService.ClusterDiagnosticsSnapshot? _diagnostics;

    public RemoteBotClusterReader(GatewayApiClient api) => _api = api;

    public IReadOnlyList<BotCluster> GetClusters()
    {
        // Cluster reads come from sync code paths in the dashboard middleware; we block
        // synchronously here, but each call only takes a small HTTP round-trip and the
        // local cache shields subsequent calls in the same render.
        return _clusters ??= FetchClustersAsync().GetAwaiter().GetResult();
    }

    public BotClusterService.ClusterDiagnosticsSnapshot GetDiagnostics()
    {
        return _diagnostics ??= FetchDiagnosticsAsync().GetAwaiter().GetResult();
    }

    /// <summary>Called by the SignalR invalidation relay to drop cached snapshots.</summary>
    internal void Invalidate()
    {
        _clusters = null;
        _diagnostics = null;
    }

    private async Task<IReadOnlyList<BotCluster>> FetchClustersAsync()
    {
        var list = await _api.GetEnvelopeAsync<List<BotCluster>>("/api/v1/clusters");
        return list ?? new List<BotCluster>();
    }

    private async Task<BotClusterService.ClusterDiagnosticsSnapshot> FetchDiagnosticsAsync()
    {
        var diag = await _api.GetEnvelopeAsync<BotClusterService.ClusterDiagnosticsSnapshot>(
            "/api/v1/clusters/diagnostics");
        return diag ?? BotClusterService.ClusterDiagnosticsSnapshot.Empty;
    }
}
