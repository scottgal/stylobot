using System.Collections.Concurrent;
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
    private readonly IOptions<DashboardMaterializerOptions> _optionsAccessor;

    // Startup-snapshot only (FOSS hard rule: no runtime options-reload -- hot-reload is
    // commercial-only). Enabled/MaxTickDurationMs/etc. are fixed at process start.
    private DashboardMaterializerOptions _options => _optionsAccessor.Value;
    private readonly ILogger<DashboardMaterializerCoordinator>? _logger;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? _hubContext;
    private readonly TimeProvider _time;
    private IDisposable? _tickSub;

    // Per-envelope "already warming" guard shared by the tick loop's wave and MarkDirtyAsync.
    // Needed because SlidingCacheAtom.GetOrComputeAsync does NOT itself serialize concurrent
    // computes for the identical key: its underlying EphemeralWorkCoordinator is a
    // concurrency-gated queue (not a keyed/per-key one), so two WarmAsync calls for the same
    // (manifest, window, tick) issued close together can both reach the compose factory before
    // either has cached a result. Whichever caller registers the Lazy<Task> first actually
    // computes; a concurrent caller for the SAME envelope awaits that SAME Task instead of
    // triggering a second compute. Keyed on envelope (manifest+window), not envelope+tick, so
    // an overlapping warm at a slightly different tick still coalesces onto the in-flight one
    // rather than racing it -- the in-flight compute is about to produce a fresh result anyway.
    private readonly ConcurrentDictionary<DashboardContentEnvelope, Lazy<Task<DashboardPageResult>>> _inFlightWarms = new();

    public DashboardMaterializerCoordinator(
        IDashboardContentCache cache,
        IDashboardChangeCursor cursor,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardMaterializerOptions> options,
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
        _optionsAccessor = options;
        _schedule = schedule;
        _logger = logger;
        _hubContext = hubContext;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable only when there's no tick fabric to subscribe to (viewer-mode host).
        // Enabled is checked inside MaterializeTickAsync instead of gating the subscription --
        // structurally simpler (one code path) even though FOSS has no runtime reload to benefit
        // from it. Subscribing unconditionally costs one no-op Task per idle 10s tick when
        // disabled -- negligible.
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
    ///     Single choke point for every warm this coordinator performs -- the tick loop's
    ///     wave and <see cref="MarkDirtyAsync"/> both call through here instead of
    ///     <see cref="IDashboardContentCache.WarmAsync"/> directly. See <see cref="_inFlightWarms"/>
    ///     for why this guard exists: the underlying cache atom does not serialize concurrent
    ///     computes of the same key on its own.
    /// </summary>
    private Task<DashboardPageResult> WarmEnvelopeAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
    {
        var envelope = DashboardContentEnvelope.From(manifest, window);
        var lazy = _inFlightWarms.GetOrAdd(envelope, _ => new Lazy<Task<DashboardPageResult>>(
            () => _cache.WarmAsync(manifest, window, tick, ct),
            LazyThreadSafetyMode.ExecutionAndPublication));

        return AwaitWarmAndClearAsync(envelope, lazy);
    }

    private async Task<DashboardPageResult> AwaitWarmAndClearAsync(
        DashboardContentEnvelope envelope, Lazy<Task<DashboardPageResult>> lazy)
    {
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            // Only clears the entry if it's still THIS in-flight warm (a later caller may
            // already have started a fresh one for the same envelope after this one cleared).
            _inFlightWarms.TryRemove(new KeyValuePair<DashboardContentEnvelope, Lazy<Task<DashboardPageResult>>>(envelope, lazy));
        }
    }

    /// <summary>
    ///     One materialization pass: warm every live envelope at the current tick.
    ///     Compute happens here, off the request thread. Budget-capped
    ///     (<see cref="DashboardMaterializerOptions.MaxPagesPerTick"/>) and
    ///     fault-isolated per envelope so one failure doesn't stop the rest.
    ///     After warming, emits SignalR invalidation beacons for changed surfaces.
    /// </summary>
    internal async Task MaterializeTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        // Checked here rather than gating the subscription in StartAsync -- keeps a single
        // code path (StartAsync always subscribes when a schedule exists; this is the gate).
        if (!_options.Enabled) return;

        var tick = _cursor.CurrentTick;

        // §7 Tier 2 (demand ranking): live envelopes ordered hottest-first using AccessCount/
        // LastAccess sourced from SlidingCacheAtom's OWN per-key tracking (DashboardContentCache.
        // LiveEnvelopes() computes these at read time via TryGetEntryStats) -- the same hotness
        // the atom already uses for its own eviction scoring, not a second counter maintained
        // alongside it. A page a request actually hammers wins the tick's budget over whatever
        // the dictionary happened to enumerate first.
        var ranked = _cache.LiveEnvelopes()
            .OrderByDescending(e => e.AccessCount)
            .ThenByDescending(e => e.LastAccess)
            .Select(e => (e.Manifest, e.Window))
            .ToList();

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
                    await WarmEnvelopeAsync(item.Manifest, item.Window, tick, ct).ConfigureAwait(false);
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

    /// <summary>
    ///     Requests an out-of-band, immediate re-warm of a specific page key, bypassing the
    ///     normal tick-driven schedule. Intended for callers reacting to an external "data
    ///     changed" signal (e.g. a SignalR push from an upstream gateway) on a BACKGROUND
    ///     service, never on a request thread (this still computes synchronously -- it's
    ///     exactly the anti-pattern Fix 1 eliminated from the request path, just relocated
    ///     to where it's actually safe: an out-of-request background caller).
    ///     <para>
    ///         "Live" is decided by the SAME registry the tick loop ranks against
    ///         (<see cref="IDashboardContentCache.LiveEnvelopes"/>) -- there is no second
    ///         registry. A page nobody is currently viewing has no live envelope, so this
    ///         is a silent no-op (returns <c>false</c>): forcing a compute for an unwatched
    ///         page would defeat the LFU-demand-gating principle the content cache relies
    ///         on (only viewed envelopes are ever warmed; unviewed ones age out). A page
    ///         can have more than one live envelope (e.g. several window tokens open at
    ///         once) -- all of them are re-warmed, but the cursor bump / broadcast still
    ///         fires exactly once for the page key, matching the tick loop's own
    ///         per-page (not per-envelope) dedup.
    ///     </para>
    ///     <para>
    ///         Respects <see cref="DashboardMaterializerOptions.Enabled"/> -- the same
    ///         startup-snapshot master switch <see cref="MaterializeTickAsync"/> checks --
    ///         so a disabled materializer has no back door around its own off switch.
    ///     </para>
    ///     <para>
    ///         Concurrency safety: this calls the SAME <see cref="WarmEnvelopeAsync"/> choke
    ///         point the tick loop's wave uses, never <see cref="IDashboardContentCache.WarmAsync"/>
    ///         directly. That matters because <c>SlidingCacheAtom.GetOrComputeAsync</c> does
    ///         NOT itself serialize concurrent computes of the identical key (its underlying
    ///         <c>EphemeralWorkCoordinator</c> is a concurrency-gated queue, not a keyed one --
    ///         two requests enqueued before either completes can both reach the compose
    ///         factory). <see cref="_inFlightWarms"/> is the real guard: if this call races the
    ///         tick loop's own warm of the identical envelope, whichever arrives first registers
    ///         the in-flight <c>Task</c> and the other one just awaits it -- one compose, not two.
    ///     </para>
    /// </summary>
    /// <param name="pageKey">The manifest page key to re-warm (e.g. <c>"dashboard.traffic"</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     <c>true</c> if the page had at least one live envelope and a warm was triggered
    ///     (and its cursor bump / broadcast queued); <c>false</c> if the page currently has
    ///     no live envelope, or the materializer is disabled -- both safe no-ops.
    /// </returns>
    public async Task<bool> MarkDirtyAsync(string pageKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pageKey);

        if (!_options.Enabled) return false;

        var matches = _cache.LiveEnvelopes()
            .Where(e => string.Equals(e.Manifest.PageKey, pageKey, StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0) return false;

        var tick = _cursor.CurrentTick;
        var warmedAny = false;

        foreach (var (manifest, window, _, _) in matches)
        {
            try
            {
                await WarmEnvelopeAsync(manifest, window, tick, ct).ConfigureAwait(false);
                warmedAny = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "DashboardMaterializerCoordinator: MarkDirtyAsync warm failed for {Page}.", pageKey);
            }
        }

        if (warmedAny && _hubContext is not null)
        {
            _cursor.Bump(pageKey);
            SignalRBroadcastConstrainer.Queue(_hubContext, pageKey, _options.MaterializerBroadcastIntervalMs);
        }

        return warmedAny;
    }
}
