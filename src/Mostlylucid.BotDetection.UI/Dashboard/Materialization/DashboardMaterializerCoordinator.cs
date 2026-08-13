using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
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
    private readonly IOptions<DashboardLayoutOptions>? _layout;
    private readonly DashboardDiagnostics? _diagnostics;

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

    // Stage 2b: per-envelope "when did this last actually get warmed" tracking, so the
    // tick loop can skip an envelope that isn't due yet (DashboardRefreshCadence). Updated
    // ONLY on a real compute (see AwaitWarmAndClearAsync) -- never on a skip -- so an
    // envelope that keeps getting skipped keeps comparing against its last REAL warm, not
    // some rolling "last considered" timestamp. Shared by the tick loop and MarkDirtyAsync
    // (both go through WarmEnvelopeAsync), so a forced out-of-band warm also resets the
    // due-time clock -- correct, since the bundle genuinely IS fresh again after it.
    private readonly ConcurrentDictionary<DashboardContentEnvelope, DateTimeOffset> _lastWarmedAt = new();

    // Stage 2b: measured-cost-vs-budget adaptive controller (see DashboardRefreshCadence
    // and DashboardMaterializerAdaptiveController for the full algorithm). Owned here
    // (not DI-registered) because it's tightly coupled to this coordinator's own tick
    // loop -- the only thing that ever measures a real tick's warm-work cost -- mirroring
    // how _inFlightWarms and _lastWarmedAt are coordinator-private state rather than
    // separately-injected services.
    private readonly DashboardMaterializerAdaptiveController _adaptive;

    // The boot-time pass task (see BootWarmCompletion). Null when disabled or no tick fabric.
    private Task? _bootWarm;

    public DashboardMaterializerCoordinator(
        IDashboardContentCache cache,
        IDashboardChangeCursor cursor,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardMaterializerOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DashboardMaterializerCoordinator>? logger = null,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? hubContext = null,
        TimeProvider? timeProvider = null,
        IOptions<DashboardLayoutOptions>? layout = null,
        DashboardDiagnostics? diagnostics = null)
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
        _layout = layout;
        _diagnostics = diagnostics;
        _adaptive = new DashboardMaterializerAdaptiveController(options);

        // Boot-time materializer pass, fired HERE (not StartAsync) so its completion is
        // deterministic for any downstream boot step (the dashboard host's L2 shingle
        // pre-render gate awaits BootWarmCompletion): hosted services start concurrently,
        // so a StartAsync-fired pass could be read as CompletedTask by a racer. The
        // tick subscription stays in StartAsync. Mirrors the commercial
        // DashboardBucketPrewarmService's constructor-fired boot pass (same rationale:
        // the tick loop's first fire waits for the next wall-clock boundary).
        if (_options.BootPrewarmEnabled && _schedule is not null)
        {
            _bootWarm = MaterializeTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            ObserveFault(_bootWarm);
        }
    }

    /// <summary>Test/diagnostic seam onto the adaptive controller's current scale factor.</summary>
    internal double CurrentAdaptiveScaleFactor => _adaptive.CurrentScaleFactor;

    // Health latch: true once ANY envelope has been composed/warmed successfully. Volatile
    // because it's written on a background warm task and read on the request thread. See the
    // set-site in AwaitWarmAndClearAsync and the PART 4 guard in StyloBotDashboardMiddleware.
    private volatile bool _hasWarmedSuccessfully;

    // When the cache was last refreshed by a successful warm (UTC). Operator directive
    // 2026-08-11: surfaced in the UI as "Updated X ago" next to the domain/period
    // selectors, so the operator sees how fresh the cached data is. Written on the warm
    // task, read on the request thread — DateTimeOffset writes are atomic.
    private DateTimeOffset? _lastWarmedAtUtc;

    /// <summary>
    ///     True once the tick materializer has warmed at least one envelope successfully --
    ///     i.e. the compose path is proven healthy. The request path gates its instant
    ///     "warming" cold-miss paint on this so a degraded host (compose always throws) is
    ///     never left warming forever with nothing to warm it; it keeps the synchronous
    ///     store fallback instead.
    /// </summary>
    public bool HasWarmedSuccessfully => _hasWarmedSuccessfully;

    /// <summary>
    ///     When the cache was last refreshed by a successful materializer warm (UTC), or
    ///     null before the first successful warm. Surfaced in the dashboard UI as the
    ///     "Updated X ago" freshness indicator (operator directive 2026-08-11) — the
    ///     cache is at most the refresh cadence (default 60s) stale.
    /// </summary>
    public DateTimeOffset? LastWarmedAtUtc => _lastWarmedAtUtc;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable only when there's no tick fabric to subscribe to (viewer-mode host).
        // Enabled is checked inside MaterializeTickAsync instead of gating the subscription --
        // structurally simpler (one code path) even though FOSS has no runtime reload to benefit
        // from it. Subscribing unconditionally costs one no-op Task per idle 10s tick when
        // disabled -- negligible.
        if (_schedule is null) return;

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

        // Boot-time cache-key contract (operator directive 2026-08-12): before any request
        // is served, assert that every top-level page's default read window resolves — through
        // the single BuildPinnedWindow derivation — to an envelope the pinned prewarm covers.
        // A violation means a read path drifted from the prewarm's envelope keys (the
        // permanent-cold-miss defect class). Fail loud at boot; never serve silent zeros.
        try
        {
            DashboardCacheKeyContract.VerifyPrewarmCoverage(
                _options,
                _manifests,
                defaultWindowMinutes: _layout?.Value?.DefaultTimeWindowMinutes ?? 1440,
                now: _time.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Dashboard cache-key contract verification failed at boot.");
            throw;
        }

        // Boot-time materializer pass (see DashboardMaterializerOptions.BootPrewarmEnabled):
        // the tick loop's first fire waits for the next wall-clock Tick10s boundary, so a
        // first request landing inside that gap would cold-miss. Fire one pass now, off the
        // request thread (hosted-service lifecycle), fault-observed, and bound-await it so
        // the first request lands on a composed pinned window. Mirrors the commercial
        // DashboardBucketPrewarmService boot pass: one implementation of "do a pass"
        // (MaterializeTickAsync), fired early because the tick loop is wall-clock aligned.
        // MaterializeTickAsync never throws out of its per-envelope fault isolation, so the
        // bound-await can never fail host startup -- a pass that overruns BootPrewarmTimeoutMs
        // keeps running in the background and the tick subscription is the standing retry.
        // Bound-await the constructor-fired boot pass (see BootWarmCompletion) so host
        // readiness — and, on this host's topology, real traffic reachability — doesn't
        // outrun the first warm. The pass itself keeps running in the background if it
        // overruns BootPrewarmTimeoutMs; the tick subscription is the standing retry.
        if (_bootWarm is not null)
        {
            var timeout = TimeSpan.FromMilliseconds(_options.BootPrewarmTimeoutMs);
            if (timeout > TimeSpan.Zero)
            {
                await Task.WhenAny(_bootWarm, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     The boot-time materializer pass's completion (see <see cref="DashboardMaterializerOptions.BootPrewarmEnabled"/>).
    ///     <see cref="Task.CompletedTask"/> when the boot pass was not run (disabled, or no tick fabric).
    ///     Downstream boot steps — e.g. the dashboard host's L2 shingle pre-render gate, which needs
    ///     the L1 data warm before it renders — await this (bounded) so ordering is explicit rather
    ///     than racing the parallel IHostedService start. Already fault-observed internally, so
    ///     awaiting it can never throw.
    /// </summary>
    public Task BootWarmCompletion => _bootWarm ?? Task.CompletedTask;

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
            var result = await lazy.Value.ConfigureAwait(false);
            // Stage 2b: record the real warm timestamp AFTER a successful compute (never on
            // a skip) so DashboardRefreshCadence's due-time check always measures from the
            // last GENUINE warm, whether it came from the tick loop or a MarkDirtyAsync force.
            // D1 (P0 2026-08-13): a poison-guard Warming result is NOT a successful compute —
            // stamping it made IsDueForWarm suppress the retry for the full refresh interval
            // (60s of stale data with no beacon). Failures stay un-stamped so the next tick
            // retries.
            if (!result.IsWarming)
            {
                _lastWarmedAt[envelope] = _time.GetUtcNow();
                // Health latch: this compose PATH is proven to work. Read by the request path
                // (dashboard-graph-quality PART 4 infinite-warming guard) so it only paints the
                // instant "warming" state for a cold-miss once the materializer has demonstrably
                // warmed SOMETHING -- on a degraded host whose compose always throws, this stays
                // false and the request path keeps its synchronous store fallback (honest data),
                // never a spinner that would warm forever with nothing to warm it.
                _hasWarmedSuccessfully = true;
                // Freshness stamp for the "Updated X ago" indicator: the last time ANY
                // envelope was actually re-warmed (a real compute, never a skip).
                _lastWarmedAtUtc = _time.GetUtcNow();
            }
            return result;
        }
        finally
        {
            // Only clears the entry if it's still THIS in-flight warm (a later caller may
            // already have started a fresh one for the same envelope after this one cleared).
            _inFlightWarms.TryRemove(new KeyValuePair<DashboardContentEnvelope, Lazy<Task<DashboardPageResult>>>(envelope, lazy));
        }
    }

    /// <summary>
    ///     Stage 2b due-time gate: has enough wall-clock time passed since this envelope was
    ///     last ACTUALLY warmed for it to be worth warming again? An envelope with no prior
    ///     warm at all (<see cref="_lastWarmedAt"/> has no entry) is always due -- matches
    ///     the pre-Stage-2b behavior of warming every live/pinned envelope the first time it
    ///     is ever seen, and is what keeps every existing single-tick coordinator test
    ///     passing unchanged (they never warm the SAME envelope across two ticks).
    /// </summary>
    private bool IsDueForWarm(DashboardContentEnvelope envelope, DashboardPageManifest manifest, int accessCount)
    {
        var intervalSeconds = DashboardRefreshCadence.ComputeEffectiveIntervalSeconds(
            manifest, accessCount, _adaptive.CurrentScaleFactor, _options);
        if (!_lastWarmedAt.TryGetValue(envelope, out var last)) return true;
        return _time.GetUtcNow() - last >= TimeSpan.FromSeconds(intervalSeconds);
    }

    /// <summary>
    ///     One materialization pass: warm every DUE live envelope at the current tick.
    ///     Compute happens here, off the request thread. Budget-capped
    ///     (<see cref="DashboardMaterializerOptions.MaxPagesPerTick"/>) and
    ///     fault-isolated per envelope so one failure doesn't stop the rest.
    ///     After warming, emits SignalR invalidation beacons for changed surfaces.
    ///     <para>
    ///         Stage 2b: "live" (or "pinned") no longer implies "warm it this tick" --
    ///         <see cref="IsDueForWarm"/> (backed by <see cref="DashboardRefreshCadence"/>)
    ///         decides whether an envelope's effective refresh interval has actually
    ///         elapsed since its last real warm. This applies to BOTH the Tier 1 pinned
    ///         coverage and the Tier 2 demand-ranked envelopes -- pinned just means "never
    ///         displaced by budget", not "immune to cadence". The tick's TOTAL measured
    ///         warm-work cost (real wall-clock time actually spent composing, summed across
    ///         every envelope warmed) is fed to <see cref="_adaptive"/> exactly once at the
    ///         end of the pass, regardless of which branch the method returns from.
    ///     </para>
    /// </summary>
    internal async Task MaterializeTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        // Checked here rather than gating the subscription in StartAsync -- keeps a single
        // code path (StartAsync always subscribes when a schedule exists; this is the gate).
        if (!_options.Enabled) return;

        _diagnostics?.RecordTick(DateTimeOffset.UtcNow);
        var tick = _cursor.CurrentTick;
        var tickCostMs = 0.0;

        try
        {
            // §7 Tier 2 (demand ranking): live envelopes ordered hottest-first using AccessCount/
            // LastAccess sourced from SlidingCacheAtom's OWN per-key tracking (DashboardContentCache.
            // LiveEnvelopes() computes these at read time via TryGetEntryStats) -- the same hotness
            // the atom already uses for its own eviction scoring, not a second counter maintained
            // alongside it. A page a request actually hammers wins the tick's budget over whatever
            // the dictionary happened to enumerate first. AccessCount also feeds the Stage 2b
            // due-time gate's LFU-hotness scaling (DashboardRefreshCadence), so it's kept
            // alongside the pair rather than projected away.
            var ranked = _cache.LiveEnvelopes()
                .OrderByDescending(e => e.AccessCount)
                .ThenByDescending(e => e.LastAccess)
                .ToList();

            var warmQueue = new List<(DashboardPageManifest Manifest, DashboardPageWindow Window, bool IsPinned)>();

            // §7 Tier 1 (pinned coverage): Traffic at every configured window token, considered
            // every tick regardless of live/demand status -- inserted first so it's never
            // displaced by the tick's budget. Generalizes the old single-window unconditional
            // prewarm to the FOSS UI's full window-switcher set. Stage 2b: "considered" every
            // tick, not "warmed" every tick -- IsDueForWarm still gates whether this tick is the
            // one that actually re-composes it. accessCount is 0 here (pinned windows aren't
            // part of LiveEnvelopes()' hotness accounting), which is the cold/unscaled case --
            // dashboard.traffic still resolves to the Live-class base interval regardless (THE
            // NAMED INVARIANT), just without any hotness-driven acceleration below it.
            if (_options.PrewarmDefaultEnvelope)
            {
                // Operator architecture (2026-08-11): prewarm the DEFAULT view of every
                // top-level page — traffic AND the four cache-gated rows — at every
                // configured window token, so the SSR reads a populated cache and
                // "Warming up" is never displayed. PrewarmPageKeys defaults to all five
                // seeded manifests; PrewarmPageKey remains the single-key back-compat form.
                var pinnedKeys = _options.PrewarmPageKeys.Count > 0
                    ? _options.PrewarmPageKeys
                    : new[] { _options.PrewarmPageKey };
                var now = _time.GetUtcNow().UtcDateTime;
                foreach (var pageKey in pinnedKeys)
                {
                    if (_manifests.For(pageKey) is not { } prewarmManifest) continue;
                    foreach (var token in _options.PrewarmWindows)
                    {
                        // Single derivation shared with page controllers (SiteController) so
                        // a page's requested envelope can never key differently than the pinned
                        // prewarm (the site-page summary-0 root cause, 2026-08-12).
                        var pinnedWindow = DashboardRoutingHelpers.BuildPinnedWindow(token, now);

                        var pinnedEnvelope = DashboardContentEnvelope.From(prewarmManifest, pinnedWindow);
                        if (IsDueForWarm(pinnedEnvelope, prewarmManifest, accessCount: 0))
                            // Pinned 7d/30d views can fan out into corpus-scale reads. Keep
                            // the pinned tier serial so startup/idle recovery never launches
                            // every standard window against the same FOSS SQLite store at once.
                            // Demand-ranked live views retain their configured wave parallelism.
                            warmQueue.Add((prewarmManifest, pinnedWindow, IsPinned: true));
                    }
                }
            }

            foreach (var e in ranked)
            {
                var envelope = DashboardContentEnvelope.From(e.Manifest, e.Window);
                if (IsDueForWarm(envelope, e.Manifest, e.AccessCount))
                    warmQueue.Add((e.Manifest, e.Window, IsPinned: false));
            }

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
            for (var start = 0; start < warmQueue.Count;)
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

                // Pinned coverage is deliberately serial: the standard 7d/30d views are
                // known to be the expensive cold reads, and launching them alongside the
                // short windows turns an idle-start prewarm into a SQLite contention burst.
                // Once the pinned tier is complete, ordinary live envelopes still use the
                // configured bounded parallelism.
                var currentWaveSize = warmQueue[start].IsPinned ? 1 : waveSize;
                var wave = warmQueue.Skip(start).Take(Math.Min(currentWaveSize, budget - warmed)).ToList();
                var waveResults = await Task.WhenAll(wave.Select(async item =>
                {
                    // Stage 2b: measures the REAL wall-clock cost of this one warm -- fed into
                    // the adaptive controller as part of the tick's total measured cost, whether
                    // the compose succeeded or threw (the time was spent either way).
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await WarmEnvelopeAsync(item.Manifest, item.Window, tick, ct).ConfigureAwait(false);
                        return (PageKey: (string?)item.Manifest.PageKey, CostMs: sw.Elapsed.TotalMilliseconds);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "DashboardMaterializerCoordinator: warm failed for {Page}.", item.Manifest.PageKey);
                        return (PageKey: (string?)null, CostMs: sw.Elapsed.TotalMilliseconds);
                    }
                })).ConfigureAwait(false);

                foreach (var (pageKey, costMs) in waveResults)
                {
                    tickCostMs += costMs;
                    if (pageKey is null) continue;
                    warmed++;
                    warmedPages.Add(pageKey);
                }

                start += wave.Count;
            }

            // Broadcast invalidation signals for warmed surfaces. The constrainer handles
            // rate-limiting (coalescing multiple signals into a single 10s flush window).
            // The cursor is bumped when signals are queued so BroadcastDirty carries the
            // tick at which these surfaces changed.
            //
            // The signals are the page's SURFACE KINDS, not the raw page key: the client
            // (sb-live-updates.js) matches a widget's data-sb-depends against the beacon's
            // dirtyKinds, and every view declares depends as a surface (summary/countries/
            // threats/... -- the DashboardFreshnessBeacon.Surfaces catalog), never a page
            // key. Queuing the raw pageKey ("dashboard.traffic") made the content-ready
            // ping unmatchable -- no widget's depends ever intersected it, so widgets that
            // cold-missed and painted the warming shell stayed warming even after this
            // coordinator composed the bundle ("the SignalR content-ready ping never
            // happens"). See PageSurfaceKindsFor for the page-key -> kinds mapping.
            if (warmedPages.Count > 0 && _hubContext is not null)
            {
                foreach (var pageKey in warmedPages)
                {
                    foreach (var kind in PageSurfaceKindsFor(pageKey))
                    {
                        _cursor.Bump(kind);
                        SignalRBroadcastConstrainer.Queue(_hubContext, kind, _options.MaterializerBroadcastIntervalMs);
                    }
                }
            }
        }
        finally
        {
            // Recorded exactly once per tick, regardless of which branch/early-return was
            // taken above -- including a tick where nothing was due (tickCostMs stays 0.0),
            // which is itself meaningful input: it lets the adaptive controller's smoothed
            // estimate relax back toward 1.0 during a genuinely quiet tick.
            _adaptive.RecordTickCost(tickCostMs);
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
            foreach (var kind in PageSurfaceKindsFor(pageKey))
            {
                _cursor.Bump(kind);
                SignalRBroadcastConstrainer.Queue(_hubContext, kind, _options.MaterializerBroadcastIntervalMs);
            }
        }

        return warmedAny;
    }

    // ---------------------------------------------------------------------------
    // Page key -> beacon surface kinds
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     Maps a warmed page key to the dashboard surface kinds its widgets declare
    ///     (<c>data-sb-depends</c>), so the content-ready beacon's dirtyKinds intersect
    ///     the client's widget dependencies. Keys are the <see cref="DashboardFreshnessBeacon.Surfaces"/>
    ///     catalog constants (one source of truth per
    ///     <c>feedback_centralised_change_detection</c>). Unknown page keys fall back to
    ///     the key itself, preserving the pre-mapping behavior for pages this table has
    ///     never seen.
    /// </summary>
    private static IReadOnlyList<string> PageSurfaceKindsFor(string pageKey)
    {
        if (_pageSurfaceKinds.TryGetValue(pageKey, out var kinds)) return kinds;
        return new[] { pageKey };
    }

    private static readonly IReadOnlyDictionary<string, string[]> _pageSurfaceKinds =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // dashboard.traffic bundles every slice of the Traffic row: the hits chart +
            // counters depend on summary, the map on countries, the endpoints table on
            // endpoints, the top-bots widget on topbots, the by-source / top-visitors
            // panels on signature, the threats card on threats, the user-agents row on
            // useragents. A warm for ANY slice re-keys the widgets whose data it feeds.
            ["dashboard.traffic"] = new[]
            {
                DashboardFreshnessBeacon.Surfaces.Summary,
                DashboardFreshnessBeacon.Surfaces.Countries,
                DashboardFreshnessBeacon.Surfaces.Endpoints,
                DashboardFreshnessBeacon.Surfaces.TopBots,
                DashboardFreshnessBeacon.Surfaces.Signature,
                DashboardFreshnessBeacon.Surfaces.Threats,
                DashboardFreshnessBeacon.Surfaces.UserAgents,
            },
            ["dashboard.topbots"] = new[]
            {
                DashboardFreshnessBeacon.Surfaces.TopBots,
                DashboardFreshnessBeacon.Surfaces.Signature,
            },
            ["dashboard.clusters"] = new[] { DashboardFreshnessBeacon.Surfaces.Clusters },
            ["dashboard.sessions"] = new[] { DashboardFreshnessBeacon.Surfaces.Sessions },
            ["dashboard.threats"] = new[] { DashboardFreshnessBeacon.Surfaces.Threats },
            ["dashboard.visitors"] = new[]
            {
                DashboardFreshnessBeacon.Surfaces.Signature,
                DashboardFreshnessBeacon.Surfaces.Countries,
                DashboardFreshnessBeacon.Surfaces.UserAgents,
            },
        };

    /// <summary>
    ///     Observes a fire-and-forget task's fault so a background failure (the boot pass,
    ///     an out-of-band warm) never surfaces as an unobserved-task exception. Same shape
    ///     as the commercial <c>DashboardBucketPrewarmService</c> / <c>TickFreshMaterializer</c>
    ///     fault observers.
    /// </summary>
    private static void ObserveFault(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }
        task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
