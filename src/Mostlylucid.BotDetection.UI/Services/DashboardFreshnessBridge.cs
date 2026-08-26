using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;
using Mostlylucid.BotDetection.WebBotAuth;

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
///     </list>
///
///     <para>
///         The meter-stream catalog surface (MeterStreamHealth) is bridged
///         by the Prometheus pack itself -- <c>AddPrometheusPack</c> registers
///         a freshness contributor so the meter-health tile stays in lock-step
///         with the catalog. UI is not hard-wired to Prometheus; the pack is
///         an optional add-on that references UI, not the reverse.
///     </para>
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
///         that has no rule store / no schedule coordinator simply skips
///         that producer arm. The bridge stays registered unconditionally;
///         the per-surface arm self-disables when its inputs are absent.
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

    // Schedule coordinator shared by the tick-driven site-health arm. The
    // meter-stream arm that also subscribed to Tick10s now lives in the
    // Prometheus pack (its own freshness contributor), so UI carries no
    // dependency on Prometheus types here.
    private readonly IScheduleCoordinator? _coordinator;

    // Site-health arm (U3): broadcasts on every Tick10s so the Traffic
    // page's site-health widget OOB-swap-refreshes in lock-step with the
    // gateway-side DegradationStoreSampler.
    private IDisposable? _siteHealthTickSubscription;

    // Public-key registry arm (Web Bot Auth): event-driven — fires the
    // "public-keys" beacon whenever the registry's hourly manifest refresh
    // raises PublicKeyRegistryRefreshedSignal. Optional so a host without the
    // detection stack (viewer-mode) skips this arm.
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? _publicKeyRefreshed;

    public DashboardFreshnessBridge(
        DashboardFreshnessBeacon beacon,
        IPolicyRuleStore? ruleStore = null,
        PolicyStackSummaryCache? policyCache = null,
        IScheduleCoordinator? coordinator = null,
        Mostlylucid.Ephemeral.TypedSignalSink<PublicKeyRegistryRefreshedSignal>? publicKeyRefreshed = null,
        ILogger<DashboardFreshnessBridge>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(beacon);
        _beacon = beacon;
        _ruleStore = ruleStore;
        _policyCache = policyCache;
        _coordinator = coordinator;
        _publicKeyRefreshed = publicKeyRefreshed;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        AttachRuleStoreHandler();
        AttachSiteHealthSubscription();
        AttachPublicKeySubscription();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DetachRuleStoreHandler();
        DetachSiteHealthSubscription();
        DetachPublicKeySubscription();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        DetachRuleStoreHandler();
        DetachSiteHealthSubscription();
        DetachPublicKeySubscription();
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

    // -------------------- Public-key registry arm ------------------------

    private void AttachPublicKeySubscription()
    {
        if (_publicKeyRefreshed is null) return;
        try { _publicKeyRefreshed.TypedSignalRaised += OnPublicKeysRefreshed; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardFreshnessBridge: failed to subscribe to the public-key refresh signal.");
        }
    }

    private void DetachPublicKeySubscription()
    {
        if (_publicKeyRefreshed is null) return;
        try { _publicKeyRefreshed.TypedSignalRaised -= OnPublicKeysRefreshed; }
        catch { /* sink already torn down */ }
    }

    private void OnPublicKeysRefreshed(Mostlylucid.Ephemeral.SignalEvent<PublicKeyRegistryRefreshedSignal> evt)
    {
        try
        {
            _beacon.BroadcastStale(DashboardFreshnessBeacon.Surfaces.PublicKeys);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardFreshnessBridge: failed to publish public-keys stale beacon.");
        }
    }

}
