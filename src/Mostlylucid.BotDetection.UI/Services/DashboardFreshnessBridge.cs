using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Producer-side bridge that wires the centralised
///     <see cref="DashboardFreshnessBeacon"/> into the upstream change
///     sources for the three pack-health surfaces:
///
///     <list type="bullet">
///         <item><description>
///             <b>Policy stack:</b> subscribes to
///             <see cref="IPolicyRuleStore.Changed"/>; on every rule
///             corpus mutation, invalidates the
///             <see cref="PolicyStackSummaryCache"/> and broadcasts the
///             <see cref="DashboardFreshnessBeacon.Surfaces.PolicyStackSummary"/>
///             surface key.
///         </description></item>
///         <item><description>
///             <b>Meter-stream catalog:</b> subscribes to
///             <see cref="TickCadence.Tick10s"/> via
///             <see cref="IScheduleCoordinator"/>; broadcasts the
///             <see cref="DashboardFreshnessBeacon.Surfaces.MeterStreamHealth"/>
///             surface key (and invalidates the
///             <see cref="MeterStreamHealthTileCache"/>) only when the
///             catalog size has changed since the previous tick. The
///             policy-stack producer is event-driven; this one is
///             tick-driven because the catalog has no native "changed"
///             event.
///         </description></item>
///     </list>
///
///     <para>
///         The ASP.NET pack hub surface is bridged separately in the
///         commercial AspNetPack project so its inventory-changed events
///         can be reached without forming a circular project reference.
///     </para>
///
///     <para>
///         Every upstream dependency is OPTIONAL per
///         <c>feedback_remote_mode_optional_di</c>: a viewer-mode host
///         that has no rule store / no meter stream / no schedule
///         coordinator simply skips that producer arm. The bridge stays
///         registered unconditionally; the per-surface arm self-disables
///         when its inputs are absent.
///     </para>
/// </summary>
public sealed class DashboardFreshnessBridge : IHostedService, IDisposable
{
    private readonly DashboardFreshnessBeacon _beacon;
    private readonly ILogger<DashboardFreshnessBridge>? _logger;

    // Policy-stack arm.
    private readonly IPolicyRuleStore? _ruleStore;
    private readonly PolicyStackSummaryCache? _policyCache;
    private EventHandler<PolicyRuleStoreChangedEventArgs>? _ruleStoreHandler;

    // Meter-stream arm.
    private readonly IMeterStream? _meterStream;
    private readonly IScheduleCoordinator? _coordinator;
    private readonly MeterStreamHealthTileCache? _meterTileCache;
    private IDisposable? _tickSubscription;
    private int _lastObservedCatalogSize = -1;

    // Site-health arm (U3): broadcasts on every Tick10s so the Traffic
    // page's site-health widget OOB-swap-refreshes in lock-step with the
    // gateway-side DegradationStoreSampler. Subscription is independent
    // of the meter-stream arm so a host that only opted into one half
    // still gets the other.
    private IDisposable? _siteHealthTickSubscription;

    public DashboardFreshnessBridge(
        DashboardFreshnessBeacon beacon,
        IPolicyRuleStore? ruleStore = null,
        PolicyStackSummaryCache? policyCache = null,
        IMeterStream? meterStream = null,
        IScheduleCoordinator? coordinator = null,
        MeterStreamHealthTileCache? meterTileCache = null,
        ILogger<DashboardFreshnessBridge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        _beacon = beacon;
        _ruleStore = ruleStore;
        _policyCache = policyCache;
        _meterStream = meterStream;
        _coordinator = coordinator;
        _meterTileCache = meterTileCache;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        AttachRuleStoreHandler();
        AttachTickSubscription();
        AttachSiteHealthSubscription();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DetachRuleStoreHandler();
        DetachTickSubscription();
        DetachSiteHealthSubscription();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DetachRuleStoreHandler();
        DetachTickSubscription();
        DetachSiteHealthSubscription();
    }

    // -------------------- Policy stack arm -------------------------------

    private void AttachRuleStoreHandler()
    {
        if (_ruleStore is null) return;

        _ruleStoreHandler = (_, _) =>
        {
            try
            {
                _policyCache?.Invalidate();
                _beacon.BroadcastStale(DashboardFreshnessBeacon.Surfaces.PolicyStackSummary);
            }
            catch (Exception ex)
            {
                // A failed broadcast must not unwind the rule-store reload
                // pipeline. The next reload (or a manual refresh) reseeds
                // the cache through the normal builder path.
                _logger?.LogWarning(ex,
                    "DashboardFreshnessBridge: failed to publish policy-stack stale beacon.");
            }
        };

        _ruleStore.Changed += _ruleStoreHandler;
    }

    private void DetachRuleStoreHandler()
    {
        if (_ruleStore is null || _ruleStoreHandler is null) return;
        _ruleStore.Changed -= _ruleStoreHandler;
        _ruleStoreHandler = null;
    }

    // -------------------- Meter-stream arm --------------------------------

    private void AttachTickSubscription()
    {
        // Tick-driven catalog detection only makes sense when BOTH the
        // coordinator AND the stream are wired. On a viewer-mode host the
        // stream may be remote-pulled, the catalog may still change, but the
        // change event is owned by the gateway -- this arm self-disables on
        // viewer-mode hosts (no schedule coordinator) by design.
        if (_coordinator is null || _meterStream is null) return;

        try
        {
            _tickSubscription = _coordinator.Subscribe(
                TickCadence.Tick10s,
                nameof(DashboardFreshnessBridge) + ".MeterStreamHealth",
                CostHint.Low,
                CheckMeterCatalogAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardFreshnessBridge: failed to subscribe to Tick10s for meter-stream health.");
        }
    }

    private void DetachTickSubscription()
    {
        try { _tickSubscription?.Dispose(); }
        catch
        {
            // Coordinator may have been torn down already; nothing to do.
        }
        _tickSubscription = null;
    }

    // -------------------- Site-health arm (U3) ---------------------------

    private void AttachSiteHealthSubscription()
    {
        // Self-disables when the host has no schedule coordinator (viewer-
        // mode hosts that never opted into the FOSS tick fabric). The
        // beacon is the only producer; consumers don't need a sampler in
        // this process — the gateway owns the sample loop.
        if (_coordinator is null) return;

        try
        {
            _siteHealthTickSubscription = _coordinator.Subscribe(
                TickCadence.Tick10s,
                nameof(DashboardFreshnessBridge) + ".SiteHealth",
                CostHint.Low,
                (_, _) =>
                {
                    try
                    {
                        _beacon.BroadcastStale(DashboardFreshnessBeacon.Surfaces.SiteHealth);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex,
                            "DashboardFreshnessBridge: failed to publish site-health stale beacon.");
                    }
                    return Task.CompletedTask;
                });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardFreshnessBridge: failed to subscribe to Tick10s for site-health.");
        }
    }

    private void DetachSiteHealthSubscription()
    {
        try { _siteHealthTickSubscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        _siteHealthTickSubscription = null;
    }

    private async Task CheckMeterCatalogAsync(DateTimeOffset _, CancellationToken ct)
    {
        if (_meterStream is null) return;

        IReadOnlyList<MeterCatalogEntry> catalog;
        try
        {
            catalog = await _meterStream.ListAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A flaky meter stream must not crash the bridge; skip this
            // tick. The previous broadcast is the most recent state the
            // client knows about; the next successful tick will catch up.
            _logger?.LogDebug(ex,
                "DashboardFreshnessBridge: meter-stream ListAsync threw; skipping tick.");
            return;
        }

        var size = catalog.Count;
        if (size == _lastObservedCatalogSize) return;

        _lastObservedCatalogSize = size;

        try
        {
            _meterTileCache?.Invalidate();
            _beacon.BroadcastStale(DashboardFreshnessBeacon.Surfaces.MeterStreamHealth);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardFreshnessBridge: failed to publish meter-stream stale beacon.");
        }
    }
}
