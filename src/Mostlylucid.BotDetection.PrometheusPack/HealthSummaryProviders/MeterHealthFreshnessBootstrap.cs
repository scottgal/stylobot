using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.PrometheusPack.HealthSummaryProviders;

/// <summary>
///     Producer-side freshness contributor for the meter-health tile, owned by
///     this pack (the widget surface for the meter catalog lives HERE, not in
///     the UI assembly).
///     <para>
///         Subscribes to <see cref="TickCadence.Tick10s"/> via
///         <see cref="IScheduleCoordinator"/>; on each tick it lists the meter
///         catalog and, when its size has changed since the previous tick,
///         invalidates the <see cref="MeterStreamHealthTileCache"/> and
///         broadcasts the <see cref="DashboardFreshnessBeacon.Surfaces.MeterStreamHealth"/>
///         surface key so connected browsers OOB-swap the tile. This is the
///         tick-driven half of the centralised beacon path
///         (feedback_centralised_change_detection): the tile provider reads the
///         cache on BuildTileAsync, the producer invalidates on change.
///     </para>
///     <para>
///         Self-disables when the host lacks the dashboard beacon, a meter
///         stream, or a schedule coordinator -- a dashboard-less or viewer-mode
///         host simply skips this arm (feedback_remote_mode_optional_di). The
///         tick-driven design mirrors the behaviour that previously lived in
///         the UI's DashboardFreshnessBridge; it moved here so UI carries no
///         dependency on Prometheus types.
///     </para>
/// </summary>
internal sealed class MeterHealthFreshnessBootstrap : IHostedService, IDisposable
{
    private readonly DashboardFreshnessBeacon? _beacon;
    private readonly IMeterStream? _stream;
    private readonly MeterStreamHealthTileCache? _cache;
    private readonly IScheduleCoordinator? _coordinator;
    private readonly ILogger<MeterHealthFreshnessBootstrap>? _logger;
    private IDisposable? _tickSubscription;
    private int _lastObservedCatalogSize = -1;

    public MeterHealthFreshnessBootstrap(
        DashboardFreshnessBeacon? beacon = null,
        IMeterStream? stream = null,
        MeterStreamHealthTileCache? cache = null,
        IScheduleCoordinator? coordinator = null,
        ILogger<MeterHealthFreshnessBootstrap>? logger = null)
    {
        _beacon = beacon;
        _stream = stream;
        _cache = cache;
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable unless this host actually has the dashboard beacon, a
        // meter stream AND a tick coordinator wired. A dashboard-less or
        // viewer-mode host skips this arm silently.
        if (_coordinator is null || _stream is null || _beacon is null)
            return Task.CompletedTask;

        try
        {
            _tickSubscription = _coordinator.Subscribe(
                TickCadence.Tick10s,
                nameof(MeterHealthFreshnessBootstrap) + ".MeterStreamHealth",
                CostHint.Low,
                CheckMeterCatalogAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "MeterHealthFreshnessBootstrap: failed to subscribe to Tick10s for meter-stream health.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try { _tickSubscription?.Dispose(); }
        catch { /* coordinator may already be torn down */ }
        _tickSubscription = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { _tickSubscription?.Dispose(); }
        catch { /* coordinator may already be torn down */ }
        _tickSubscription = null;
    }

    private async Task CheckMeterCatalogAsync(DateTimeOffset _, CancellationToken ct)
    {
        if (_stream is null) return;

        IReadOnlyList<MeterCatalogEntry> catalog;
        try
        {
            catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A flaky meter stream must not crash the tick loop; skip this
            // tick. The previous broadcast is the most recent state the client
            // knows about; the next successful tick will catch up.
            _logger?.LogDebug(ex,
                "MeterHealthFreshnessBootstrap: meter-stream ListAsync threw; skipping tick.");
            return;
        }

        var size = catalog.Count;
        if (size == _lastObservedCatalogSize) return;

        _lastObservedCatalogSize = size;

        try
        {
            _cache?.Invalidate();
            _beacon?.BroadcastStale(DashboardFreshnessBeacon.Surfaces.MeterStreamHealth);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "MeterHealthFreshnessBootstrap: failed to publish meter-stream stale beacon.");
        }
    }
}
