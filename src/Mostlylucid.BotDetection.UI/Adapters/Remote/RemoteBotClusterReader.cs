using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only cluster surface that proxies to the gateway's <c>/api/v1/clusters</c>.
///     Pure async; <see cref="IBotClusterReader"/> is async so the dashboard middleware
///     awaits the HTTP round-trip instead of blocking thread-pool threads via
///     <c>.GetAwaiter().GetResult()</c>.
///
///     <para>
///     Holds a volatile cache of the last fetched snapshot. <see cref="Invalidate"/> is
///     called by the SignalR invalidation relay when Phase 4 lands; until then the cache
///     lives until the next process restart. Stale-data acceptable for a viewer-only
///     dashboard.
///     </para>
/// </summary>
internal sealed class RemoteBotClusterReader : IBotClusterReader
{
    private readonly GatewayApiClient _api;
    private volatile IReadOnlyList<BotCluster>? _clusters;
    private volatile BotClusterService.ClusterDiagnosticsSnapshot? _diagnostics;

    public RemoteBotClusterReader(GatewayApiClient api) => _api = api;

    public async Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default)
    {
        if (_clusters is not null) return _clusters;
        _clusters = await _api.GetEnvelopeListAsync<BotCluster>("/api/v1/clusters", ct);
        return _clusters;
    }

    public async Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        if (_diagnostics is not null) return _diagnostics;
        var diag = await _api.GetEnvelopeAsync<BotClusterService.ClusterDiagnosticsSnapshot>(
            "/api/v1/clusters/diagnostics", ct);
        _diagnostics = diag ?? BotClusterService.ClusterDiagnosticsSnapshot.Empty;
        return _diagnostics;
    }

    /// <summary>Called by the SignalR invalidation relay (Phase 4) to drop cached snapshots.</summary>
    internal void Invalidate()
    {
        _clusters = null;
        _diagnostics = null;
    }
}
