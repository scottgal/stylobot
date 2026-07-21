using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     The tick-driven materializer — the piece that moves dashboard compute OFF the
///     request thread. Each <see cref="TickCadence.Tick10s"/> it warms every live
///     envelope (the views read recently enough to still matter) into the content
///     cache at the current tick, so the request path reads a ready bundle instead of
///     composing. This is the Dashboard Coordinator's batch side.
///
///     <para>
///         Mirrors <c>DashboardFreshnessBridge</c>: an <see cref="IHostedService"/>
///         that subscribes to the schedule coordinator and self-disables on a
///         viewer-mode host with no coordinator (<c>feedback_remote_mode_optional_di</c>).
///         It is a SEPARATE coordinator (single responsibility: warm the content
///         cache) rather than another arm of the freshness bridge, which
///         <i>invalidates</i> tile caches — the opposite operation on a different
///         cache. Broadcasting stays with the existing beacon path (Plan 3): this
///         only keeps the cache warm so those beacon-driven reads hit.
///     </para>
///
///     <para>
///         Demand-gating today is the cache's live-envelope set (only viewed
///         envelopes, aged out when no longer read). SignalR presence
///         (<c>dashboard.hot_widget_keys</c>) is the precise replacement and slots in
///         behind the same <c>LiveEnvelopes()</c> seam.
///     </para>
/// </summary>
public sealed class DashboardMaterializerCoordinator : IHostedService, IDisposable
{
    private readonly IDashboardContentCache _cache;
    private readonly IDashboardChangeCursor _cursor;
    private readonly IScheduleCoordinator? _schedule;
    private readonly DashboardMaterializerOptions _options;
    private readonly ILogger<DashboardMaterializerCoordinator>? _logger;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? _hubContext;
    private IDisposable? _tickSub;

    public DashboardMaterializerCoordinator(
        IDashboardContentCache cache,
        IDashboardChangeCursor cursor,
        IOptions<DashboardMaterializerOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DashboardMaterializerCoordinator>? logger = null,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? hubContext = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(cursor);
        _cache = cache;
        _cursor = cursor;
        _options = options.Value;
        _schedule = schedule;
        _logger = logger;
        _hubContext = hubContext;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable when turned off or on a viewer-mode host with no tick fabric.
        if (!_options.Enabled || _schedule is null) return Task.CompletedTask;

        try
        {
            _tickSub = _schedule.Subscribe(
                TickCadence.Tick10s,
                nameof(DashboardMaterializerCoordinator),
                CostHint.Medium,
                MaterializeTickAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DashboardMaterializerCoordinator: failed to subscribe to Tick10s.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickSub?.Dispose();
        _tickSub = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _tickSub?.Dispose();

    /// <summary>
    ///     One materialization pass: warm every live envelope at the current tick.
    ///     Compute happens here, off the request thread. Budget-capped
    ///     (<see cref="DashboardMaterializerOptions.MaxPagesPerTick"/>) and
    ///     fault-isolated per envelope so one failure doesn't stop the rest.
    ///     After warming, emits SignalR invalidation beacons for changed surfaces.
    /// </summary>
    internal async Task MaterializeTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        var tick = _cursor.CurrentTick;
        var live = _cache.LiveEnvelopes();
        if (live.Count == 0) return;

        var budget = _options.MaxPagesPerTick;
        var warmed = 0;
        var warmedPages = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (manifest, window) in live)
        {
            if (warmed >= budget)
            {
                _logger?.LogDebug(
                    "DashboardMaterializerCoordinator: MaxPagesPerTick={Budget} reached; {Deferred} live envelope(s) deferred to next tick.",
                    budget, live.Count - warmed);
                break;
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                await _cache.WarmAsync(manifest, window, tick, ct).ConfigureAwait(false);
                warmed++;
                warmedPages.Add(manifest.PageKey);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "DashboardMaterializerCoordinator: warm failed for {Page}.", manifest.PageKey);
            }
        }

        // Broadcast invalidation signals for warmed surfaces. The constrainer handles
        // rate-limiting (coalescing multiple signals into a single 10s flush window).
        // The cursor is bumped when signals are queued so BroadcastDirty carries the
        // tick at which these surfaces changed.
        if (warmedPages.Count > 0 && _hubContext is not null)
        {
            foreach (var pageKey in warmedPages)
            {
                _cursor.Bump(pageKey);
                SignalRBroadcastConstrainer.Queue(_hubContext, pageKey, _options.MaterializerBroadcastIntervalMs);
            }
        }
    }
}
