using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Common.Scheduling;
using Stylobot.Gateway.Data;
using Yarp.ReverseProxy.Configuration;

namespace Stylobot.Gateway.Health;

/// <summary>
/// Subscribes to the <see cref="TickCadence.Tick1m"/> tick and probes each YARP
/// cluster's discovered health endpoint. Results are written to
/// <see cref="IActiveUpstreamProbeState"/> (the active lane) and persisted to
/// <see cref="DestinationEntity.Health"/> via a scoped <see cref="GatewayDbContext"/>.
/// <para>
/// <b>Separate-lane invariant:</b> this service has no dependency on
/// <c>DegradationAtom</c> and must never call <c>DegradationAtom.RecordResponse</c>.
/// Active probes are a synthetic out-of-band signal; passive EWMA sampling happens
/// in the YARP response pipeline, not here.
/// </para>
/// <para>
/// Pattern mirrors <c>ProfileAnalysisWorker</c>: subscribes in ctor when
/// <see cref="UpstreamHealthMonitorOptions.Enabled"/> is true; <c>OnTickAsync</c>
/// is <c>internal</c> and tested directly.
/// </para>
/// </summary>
public sealed class UpstreamHealthProbeService : IDisposable
{
    private readonly IUpstreamHealthEndpointDiscovery _discovery;
    private readonly IActiveUpstreamProbeState _probeState;
    private readonly IOptions<UpstreamHealthMonitorOptions> _options;
    private readonly IProxyConfigProvider _proxyConfig;
    private readonly HttpClient _httpClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UpstreamHealthProbeService> _logger;
    private readonly IDisposable? _subscription;

    private int _disposed;
    private DateTimeOffset _lastProbeUtc = DateTimeOffset.MinValue;

    public UpstreamHealthProbeService(
        IUpstreamHealthEndpointDiscovery discovery,
        IActiveUpstreamProbeState probeState,
        IOptions<UpstreamHealthMonitorOptions> options,
        IProxyConfigProvider proxyConfig,
        HttpClient httpClient,
        IServiceScopeFactory scopeFactory,
        ILogger<UpstreamHealthProbeService> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _discovery = discovery;
        _probeState = probeState;
        _options = options;
        _proxyConfig = proxyConfig;
        _httpClient = httpClient;
        _scopeFactory = scopeFactory;
        _logger = logger;

        if (_options.Value.Enabled && scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m,
                "UpstreamHealthProbe",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    /// Probes every YARP cluster whose health endpoint is known (cached or
    /// discovered on this tick). Each cluster is fenced by an independent
    /// try/catch so one cluster's failure does not prevent siblings from
    /// being probed.
    /// </summary>
    internal async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        // Honor ProbeIntervalSeconds: skip if we probed too recently.
        if (now - _lastProbeUtc < TimeSpan.FromSeconds(_options.Value.ProbeIntervalSeconds))
            return;

        _lastProbeUtc = now;

        var clusters = _proxyConfig.GetConfig().Clusters;
        foreach (var cluster in clusters)
        {
            try
            {
                await ProbeClusterAsync(cluster, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutdown -- stop probing; don't log noise or probe siblings.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Upstream health probe failed for cluster {ClusterId}; skipping this tick",
                    cluster.ClusterId);
            }
        }
    }

    private async Task ProbeClusterAsync(
        ClusterConfig cluster, DateTimeOffset now, CancellationToken ct)
    {
        if (cluster.Destinations is null || cluster.Destinations.Count == 0)
            return;

        var (destinationId, dest) = cluster.Destinations.First();

        // Resolve endpoint: hot-path hit first, then async discovery.
        var ep = _discovery.GetCached(cluster.ClusterId)
            ?? await _discovery.DiscoverAsync(cluster.ClusterId, dest.Address, ct);

        if (ep is null)
        {
            _logger.LogDebug(
                "No health endpoint discoverable for cluster {ClusterId}; skipping probe",
                cluster.ClusterId);
            return;
        }

        var url = $"{dest.Address.TrimEnd('/')}{ep.Path}";

        string status;
        string? reason = null;
        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(_options.Value.ProbeTimeoutMs));

            using var response = await _httpClient.GetAsync(url, cts.Token);
            sw.Stop();

            if (response.StatusCode == HttpStatusCode.OK)
            {
                status = "healthy";
            }
            else
            {
                status = "unhealthy";
                reason = $"HTTP {(int)response.StatusCode}";
                _discovery.Invalidate(cluster.ClusterId);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Per-probe timeout fired -- not a host-wide cancellation.
            sw.Stop();
            status = "unhealthy";
            reason = "timeout";
            _discovery.Invalidate(cluster.ClusterId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown -- propagate so OnTickAsync stops the loop cleanly
            // instead of recording a bogus "unhealthy" result and persisting it.
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            status = "unhealthy";
            var msg = ex.Message;
            reason = msg.Length > 120 ? msg[..120] : msg;
            _discovery.Invalidate(cluster.ClusterId);
        }

        var latencyMs = (int)sw.ElapsedMilliseconds;
        var snapshot = new ActiveProbeSnapshot(status, latencyMs, now, reason);
        _probeState.Update(cluster.ClusterId, snapshot);

        await PersistEnvelopeAsync(cluster.ClusterId, destinationId, dest.Address, snapshot, ct);
    }

    private async Task PersistEnvelopeAsync(
        string clusterId,
        string destinationId,
        string address,
        ActiveProbeSnapshot snapshot,
        CancellationToken ct)
    {
        var envelope = new HealthEnvelope(
            snapshot.Status,
            snapshot.LatencyMs,
            snapshot.CheckedAtUtc,
            snapshot.FailureReason);

        var json = JsonSerializer.Serialize(envelope, HealthEnvelope.CamelCaseOptions);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();

            // Ensure the parent ClusterEntity exists before writing the Destination FK.
            // EF Core 8+ enables SQLite FK enforcement; without this the insert would fail.
            var cluster = await db.Clusters.FindAsync(new object[] { clusterId }, ct);
            if (cluster is null)
            {
                cluster = new ClusterEntity { ClusterId = clusterId };
                db.Clusters.Add(cluster);
            }

            var entity = await db.Destinations
                .FindAsync(new object[] { clusterId, destinationId }, ct);

            if (entity is null)
            {
                entity = new DestinationEntity
                {
                    ClusterId = clusterId,
                    DestinationId = destinationId,
                    Address = address,
                };
                db.Destinations.Add(entity);
            }

            entity.Health = json;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist health envelope for {ClusterId}/{DestinationId}",
                clusterId, destinationId);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
