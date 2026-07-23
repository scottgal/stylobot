using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
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
    private readonly IDashboardPageManifestSource _manifests;
    private readonly IScheduleCoordinator? _schedule;
    private readonly IOptionsMonitor<DashboardMaterializerOptions> _optionsMonitor;

    // Live-read (not a startup snapshot) so Enabled/MaxTickDurationMs/etc. can be changed
    // via config reload without a restart -- see the Enabled handling in MaterializeTickAsync.
    private DashboardMaterializerOptions _options => _optionsMonitor.CurrentValue;
    private readonly ILogger<DashboardMaterializerCoordinator>? _logger;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? _hubContext;
    private readonly TimeProvider _time;
    private IDisposable? _tickSub;

    public DashboardMaterializerCoordinator(
        IDashboardContentCache cache,
        IDashboardChangeCursor cursor,
        IDashboardPageManifestSource manifests,
        IOptionsMonitor<DashboardMaterializerOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DashboardMaterializerCoordinator>? logger = null,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? hubContext = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(options);
        _cache = cache;
        _cursor = cursor;
        _manifests = manifests;
        _optionsMonitor = options;
        _schedule = schedule;
        _logger = logger;
        _hubContext = hubContext;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable only when there's no tick fabric to subscribe to (viewer-mode host).
        // Enabled is deliberately NOT checked here: it's read live inside MaterializeTickAsync
        // instead, so an operator can flip it off/on via config reload (the exact incident
        // stabiliser this subsystem needed) without a process restart. Subscribing
        // unconditionally costs one no-op Task per idle 10s tick when disabled -- negligible.
        if (_schedule is null) return Task.CompletedTask;

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
        // Read live so an operator can disable/re-enable via config reload without a
        // restart (StartAsync always subscribes when a schedule exists; this is the gate).
        if (!_options.Enabled) return;

        var tick = _cursor.CurrentTick;

        // §7 Tier 2 (demand ranking) is INTENTIONALLY NOT ranked yet: the operator's hard
        // constraint is no parasitic hit-counter store -- ranking must derive from the hotness
        // SlidingCacheAtom (the content cache's own LFU) already tracks per key (AccessCount/
        // LastAccess), not a second counter maintained alongside it. That atom has no public
        // per-key accessor today; adding one is a cross-repo change (mostlylucid.atoms sibling
        // repo + package version bump), gated separately. Until then this stays unranked --
        // exactly the pre-§7 behavior -- rather than shipping a parallel counter that can drift.
        var ranked = _cache.LiveEnvelopes().ToList();

        var warmQueue = new List<(DashboardPageManifest Manifest, DashboardPageWindow Window)>();

        // §7 Tier 1 (pinned coverage): Traffic at every configured window token, warmed every
        // tick regardless of live/demand status -- inserted first so it's never displaced by
        // the tick's budget. Generalizes the old single-window unconditional prewarm to the
        // FOSS UI's full window-switcher set.
        if (_options.PrewarmDefaultEnvelope && _manifests.For(_options.PrewarmPageKey) is { } prewarmManifest)
        {
            var now = DateTime.UtcNow;
            foreach (var token in _options.PrewarmWindows)
            {
                var minutes = DashboardRoutingHelpers.WindowTokenToMinutes(token, fallbackMinutes: 1440);
                var pinnedWindow = new DashboardPageWindow(
                    StartTime: now.AddMinutes(-minutes),
                    EndTime: now,
                    AudienceFilter: "all",
                    ProbMin: null,
                    Domains: null,
                    TopN: 500,
                    BucketMinutes: (int)HitsPerPeriodChartletBuilder.BucketSizeForWindow(token).TotalMinutes);
                warmQueue.Add((prewarmManifest, pinnedWindow));
            }
        }

        warmQueue.AddRange(ranked);

        if (warmQueue.Count == 0) return;

        var budget = _options.MaxPagesPerTick;
        var warmed = 0;
        var warmedPages = new HashSet<string>(StringComparer.Ordinal);
        var deadline = _time.GetUtcNow() + TimeSpan.FromMilliseconds(_options.MaxTickDurationMs);
        var waveSize = Math.Max(1, _options.MaxConcurrentWarmsPerTick);

        // §7 Tier 3 (bounded parallelism): warm in waves of at most MaxConcurrentWarmsPerTick
        // concurrent composes, mirroring ScheduleCoordinator's own bounded-parallelism pattern.
        // MaxTickDurationMs is checked BETWEEN waves (not per item within a wave) -- count alone
        // doesn't bound cost when compose cost isn't uniform (a corpus-scale query regression),
        // so once elapsed exceeds the budget the remaining envelopes defer to the next tick
        // rather than grinding through the whole queue regardless of how slow composes have become.
        for (var start = 0; start < warmQueue.Count; start += waveSize)
        {
            if (warmed >= budget)
            {
                _logger?.LogDebug(
                    "DashboardMaterializerCoordinator: MaxPagesPerTick={Budget} reached; {Deferred} envelope(s) deferred to next tick.",
                    budget, warmQueue.Count - start);
                break;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                _logger?.LogWarning(
                    "DashboardMaterializerCoordinator: MaxTickDurationMs={Budget}ms exceeded after warming {Warmed}; {Deferred} envelope(s) deferred to next tick.",
                    _options.MaxTickDurationMs, warmed, warmQueue.Count - start);
                break;
            }

            ct.ThrowIfCancellationRequested();

            var wave = warmQueue.Skip(start).Take(Math.Min(waveSize, budget - warmed)).ToList();
            var waveResults = await Task.WhenAll(wave.Select(async item =>
            {
                try
                {
                    await _cache.WarmAsync(item.Manifest, item.Window, tick, ct).ConfigureAwait(false);
                    return item.Manifest.PageKey;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "DashboardMaterializerCoordinator: warm failed for {Page}.", item.Manifest.PageKey);
                    return null;
                }
            })).ConfigureAwait(false);

            foreach (var pageKey in waveResults)
            {
                if (pageKey is null) continue;
                warmed++;
                warmedPages.Add(pageKey);
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
