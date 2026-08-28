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
    private readonly IDashboardPrewarmDomains? _prewarmDomains;

    // Startup-snapshot only (FOSS hard rule: no runtime options-reload -- hot-reload is
    // commercial-only). Enabled/MaxTickDurationMs/etc. are fixed at process start.
    private DashboardMaterializerOptions _options => _optionsAccessor.Value;
    private readonly ILogger<DashboardMaterializerCoordinator>? _logger;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub>? _hubContext;
    private readonly TimeProvider _time;
    private IDisposable? _tickSub;

    // 1 while the StartAsync-fired boot pass runs (the pinned wave bump is boot-scoped).
    private int _bootPassActive;

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
        DashboardDiagnostics? diagnostics = null,
        IDashboardPrewarmDomains? prewarmDomains = null)
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
        _prewarmDomains = prewarmDomains;
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
            // Boot-pass pinned wave bump (operator directive 2026-08-14): the first pass
            // runs the pinned tier at BootPinnedWarmConcurrency so all five standard
            // windows prerender within seconds of boot (the serial steady-state pinned
            // tier took 30+ seconds on the remote host). Background — never blocks boot.
            Interlocked.Exchange(ref _bootPassActive, 1);
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

    // Liveness watchdog (P0 2026-08-28, the silent warm-inactive class): the last wall-clock
    // moment a MaterializeTickAsync STARTED (UTC ticks; 0 = never ticked). If this stops advancing
    // (a dead schedule subscription / tick stall), the watchdog below detects it and re-arms the tick
    // instead of freezing LastWarmedAtUtc and serving a silently-stale cache until a restart.
    // Interlocked so the tick thread writes and the request thread / watchdog read atomically.
    private long _lastTickUtcTicks;
    private readonly CancellationTokenSource _watchdogCts = new();

    /// <summary>
    ///     True once the tick materializer has warmed at least one envelope successfully --
    ///     i.e. the compose path is proven healthy. The request path gates its instant
    ///     "warming" cold-miss paint on this so a degraded host (compose always throws) is
    ///     never left warming forever with nothing to warm it; it keeps the synchronous
    ///     store fallback instead.
    /// </summary>
    public bool HasWarmedSuccessfully => _hasWarmedSuccessfully;

    /// <summary>
    ///     Liveness state (P0 2026-08-28): true when the tick has been warm-inactive longer than
    ///     <see cref="DashboardMaterializerOptions.WarmInactivityThresholdSeconds"/> (a dead schedule
    ///     subscription / tick stall). The request path reads this to trigger a synchronous stale
    ///     re-warm (capping a warm-inactivity stall to one request); the internal watchdog re-arms the
    ///     tick on the same signal.
    /// </summary>
    public bool IsWarmInactive =>
        _options.WarmInactivityThresholdSeconds > 0
        && Interlocked.Read(ref _lastTickUtcTicks) is var lastTicks
        && lastTicks != 0
        && _time.GetUtcNow().UtcTicks - lastTicks
           > TimeSpan.FromSeconds(_options.WarmInactivityThresholdSeconds).Ticks;

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

        // Liveness watchdog (P0 2026-08-28, the silent warm-inactive class): fire-and-forget
        // loop that detects a DEAD TICK — _lastTickUtc not advancing for WarmInactivityThresholdSeconds
        // (a lost schedule subscription / tick stall). It logs LOUDLY and RE-ARMS the tick
        // (re-subscribe + force one MaterializeTickAsync) so the coordinator self-heals instead of
        // freezing LastWarmedAtUtc and serving a silently-stale cache until a restart. Cancelled in
        // StopAsync. The check interval is half the threshold so detection lands within the window.
        if (_options.Enabled && _options.WarmInactivityThresholdSeconds > 0)
        {
            _ = RunWatchdogAsync(_watchdogCts.Token);
        }
    }

    /// <summary>Liveness watchdog body: poll _lastTickUtc; if it stops advancing, log LOUD + re-arm.</summary>
    private async Task RunWatchdogAsync(CancellationToken ct)
    {
        var threshold = TimeSpan.FromSeconds(_options.WarmInactivityThresholdSeconds);
        var checkEvery = TimeSpan.FromSeconds(Math.Max(5, _options.WarmInactivityThresholdSeconds / 2));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(checkEvery, ct).ConfigureAwait(false);
                if (!_options.Enabled) continue;

                var lastTickTicks = Interlocked.Read(ref _lastTickUtcTicks);
                if (lastTickTicks == 0) continue; // not started yet — the boot pass stamps it
                var lastTick = new DateTimeOffset(lastTickTicks, TimeSpan.Zero);

                var idle = _time.GetUtcNow() - lastTick;
                if (idle <= threshold) continue;

                _logger?.LogError(
                    "DashboardMaterializerCoordinator: WARM-INACTIVE for {IdleSeconds:F0}s (last tick {LastTick:o}, threshold {Threshold}s) — the tick loop is dead (lost schedule subscription or a stall). Re-arming the tick so the dashboard cache stops going silently stale. If this repeats, check the schedule fabric / compose path.",
                    idle.TotalSeconds, lastTick, _options.WarmInactivityThresholdSeconds);

                await ReArmTickAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DashboardMaterializerCoordinator: liveness watchdog failed; will retry next interval.");
        }
    }

    /// <summary>Re-arm a dead tick: dispose + re-subscribe the schedule tick, then force one warm now so
    ///     the cache refreshes even if the schedule fabric itself is down (the forced pass self-heals).</summary>
    private async Task ReArmTickAsync(CancellationToken ct)
    {
        try
        {
            var schedule = _schedule;
            _tickSub?.Dispose();
            _tickSub = null;
            if (schedule is not null)
            {
                try
                {
                    _tickSub = schedule.Subscribe(
                        TickCadence.Tick10s,
                        nameof(DashboardMaterializerCoordinator),
                        CostHint.Medium,
                        MaterializeTickAsync);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "DashboardMaterializerCoordinator: re-subscribe after warm-inactive failed; the forced pass below still warms.");
                }
            }
            // Force one warm now regardless of the schedule — the cache must not wait for the next tick.
            await MaterializeTickAsync(DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
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
        _watchdogCts.Cancel();
        _tickSub?.Dispose();
        _tickSub = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _watchdogCts.Cancel();
        _watchdogCts.Dispose();
        _tickSub?.Dispose();
    }

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

    /// <summary>
    ///     Bounds how long this coordinator WAITS on a single envelope's compose (see
    ///     <see cref="DashboardMaterializerOptions.ComposeTimeoutMs"/> for the full rationale
    ///     -- the 20h prod-wedge fix, 2026-08-21). Does not cancel the underlying
    ///     <c>_cache.WarmAsync</c> call (it may not support cancellation at all, and the
    ///     coordinator has no way to force an external store call to abort); it only stops
    ///     WAITING on it past the bound, so a hung compose can never poison
    ///     <see cref="_inFlightWarms"/> for the process lifetime or block the tick loop's
    ///     <c>Task.WhenAll</c> from ever returning.
    /// </summary>
    private async Task<DashboardPageResult> AwaitWithComposeTimeoutAsync(
        DashboardContentEnvelope envelope, Lazy<Task<DashboardPageResult>> lazy)
    {
        var timeoutMs = _options.ComposeTimeoutMs;
        if (timeoutMs <= 0) return await lazy.Value.ConfigureAwait(false);

        var winner = await Task.WhenAny(lazy.Value, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (winner != lazy.Value)
        {
            _logger?.LogWarning(
                "DashboardMaterializerCoordinator: compose for {PageKey} ({Envelope}) exceeded ComposeTimeoutMs={TimeoutMs}ms; abandoning the wait so this envelope is not poisoned forever. The underlying compose may still be running in the background.",
                envelope.PageKey, envelope, timeoutMs);
            // The abandoned task may still complete (or fault) later, off in the background --
            // observe it so that never surfaces as an unobserved-task exception.
            ObserveFault(lazy.Value);
            throw new TimeoutException(
                $"Dashboard compose for '{envelope.PageKey}' exceeded ComposeTimeoutMs={timeoutMs}ms and was abandoned.");
        }

        return await lazy.Value.ConfigureAwait(false);
    }

    private async Task<DashboardPageResult> AwaitWarmAndClearAsync(
        DashboardContentEnvelope envelope, Lazy<Task<DashboardPageResult>> lazy)
    {
        try
        {
            var result = await AwaitWithComposeTimeoutAsync(envelope, lazy).ConfigureAwait(false);
            // Stage 2b: record the real warm timestamp AFTER a successful compute (never on
            // a skip) so DashboardRefreshCadence's due-time check always measures from the
            // last GENUINE warm, whether it came from the tick loop or a MarkDirtyAsync force.
            // D1 (P0 2026-08-13): a poison-guard Warming result is NOT a successful compute —
            // stamping it made IsDueForWarm suppress the retry for the full refresh interval
            // (60s of stale data with no beacon). Failures get a SHORT backoff instead of
            // staying un-stamped AND instead of the full interval: un-stamped they re-attempt
            // every tick (fine), but a stamp at now+FailureRetryBackoff means a failed
            // envelope is due again within seconds — the 60s due-window previously throttled
            // the failed set to one attempt per interval (the staging 14-cold-forever class,
            // 2026-08-14).
            if (!result.IsWarming)
            {
                _lastWarmedAt[envelope] = _time.GetUtcNow();
                // The L2 decision's baseline (2026-08-15): the cursor's tick at this
                // successful re-roll — the next re-roll requires a surface to advance
                // past it.
                _lastRolledCursorTick[envelope] = _cursor.CurrentTick;
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
            else if (_options.FailureRetryBackoffSeconds > 0)
            {
                // A failed compose is due again after a SHORT backoff (not the full refresh
                // interval, not the every-tick hammer). The due-gate's check is
                // `now - last >= interval` — stamp the envelope as if it warmed
                // (interval - backoff) ago, so the elapsed reaches the interval in
                // `backoff` seconds and the envelope is due again within seconds instead of
                // throttled to the 60s cadence. The interval's floor (GlobalMinIntervalSeconds,
                // 60 for the Live class that the traffic envelopes use) is the stamp's basis;
                // Aggregate-class envelopes (300s) keep the full interval between attempts.
                var intervalSeconds = Math.Max(1, _options.GlobalMinIntervalSeconds);
                var backoff = Math.Min(_options.FailureRetryBackoffSeconds, intervalSeconds - 1);
                _lastWarmedAt[envelope] = _time.GetUtcNow()
                    - TimeSpan.FromSeconds(intervalSeconds - backoff);
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
    // The L1 change surfaces the L2 re-roll decision watches (the middleware bumps
    // these per detection — the centralised change-detection cursor).
    private static readonly string[] ReRollSurfaces = ["signature", "summary", "threats"];

    // The cursor's tick at each envelope's last successful re-roll (the L2 decision's
    // baseline — the envelope re-rolls ONLY when a surface advanced past this).
    private readonly ConcurrentDictionary<DashboardContentEnvelope, long> _lastRolledCursorTick = new();

    private bool IsDueForWarm(DashboardContentEnvelope envelope, DashboardPageManifest manifest, int accessCount)
    {
        // The L2 re-roll decision (operator 2026-08-15 — "when L1 has a new report, L2
        // decides if it needs to re-roll"): the envelope re-rolls when the L1's change
        // surfaces advanced since its last roll (the content-change decision — L2's
        // call, not L1's, not the clock's). The refresh cadence remains ONLY as the
        // staleness floor (a never-bumped surface still refreshes on the cadence —
        // the "replaced not emptied" guarantee's bound), never as the trigger — the
        // per-tick churn is gone: an unchanged envelope keeps its existing complete
        // page.
        if (_lastRolledCursorTick.TryGetValue(envelope, out var lastRolled))
        {
            var surfacesChanged = false;
            foreach (var surface in ReRollSurfaces)
            {
                if (_cursor.TickFor(surface) > lastRolled) { surfacesChanged = true; break; }
            }
            if (surfacesChanged) return true;
        }

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

        // Liveness stamp: the watchdog uses this to detect a dead tick (no advancement).
        Interlocked.Exchange(ref _lastTickUtcTicks, _time.GetUtcNow().UtcTicks);

        // Boot-pass detection: the StartAsync-fired first pass owns the pinned wave bump
        // (BootPinnedWarmConcurrency); steady-state ticks keep the serial pinned tier.
        var isBootPass = Volatile.Read(ref _bootPassActive) == 1;

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

                        // P0 2026-08-16 (the operator's domain-scoped cold view): the pinned
                        // law must cover EVERY managed domain, not just the all-domains key.
                        // The domain-scoped envelopes (manifest, window, domains=[X]) are
                        // distinct cache keys the old loop never warmed — a domain-pill
                        // selection read a forever-cold envelope (summary 0/0/0 + "No data
                        // in this window" while the store had the rows). The seam is
                        // nullable: no provider (FOSS-only hosts) keeps all-domains-only.
                        if (_prewarmDomains is { } domains)
                        {
                            foreach (var domain in domains.GetManagedDomains())
                            {
                                if (string.IsNullOrWhiteSpace(domain)) continue;
                                var domainWindow = DashboardRoutingHelpers.BuildPinnedWindow(
                                    token, now, new[] { domain });
                                var domainEnvelope = DashboardContentEnvelope.From(prewarmManifest, domainWindow);
                                if (IsDueForWarm(domainEnvelope, prewarmManifest, accessCount: 0))
                                    warmQueue.Add((prewarmManifest, domainWindow, IsPinned: true));
                            }
                        }
                    }
                }
            }

            foreach (var e in ranked)
            {
                var envelope = DashboardContentEnvelope.From(e.Manifest, e.Window);
                if (IsDueForWarm(envelope, e.Manifest, e.AccessCount))
                    warmQueue.Add((e.Manifest, e.Window, IsPinned: false));
            }

            // The silent-empty signature (2026-08-14 staging: ticks advance, zero coordinator
            // lines, no stamps, no requests — the queue was empty every tick and the return
            // was invisible). The queue-empty must never be silent: log the per-tier counts so
            // a next-tick probe names WHY (pinned skipped vs due-gate vs no manifests).
            if (warmQueue.Count == 0)
            {
                var pinnedQueued = _options.PrewarmDefaultEnvelope
                    ? _options.PrewarmPageKeys.Count(k => _manifests.For(k) is not null) * _options.PrewarmWindows.Count
                    : 0;
                _logger?.LogDebug(
                    "DashboardMaterializerCoordinator: warm queue empty this tick (pinned eligible={Pinned}, live={Live}, enabled={Enabled}, prewarmDefault={PrewarmDefault}).",
                    pinnedQueued, ranked.Count, _options.Enabled, _options.PrewarmDefaultEnvelope);
                _diagnostics?.RecordQueueState(queueEmpty: true, pinnedQueued, ranked.Count);
                return;
            }

            var budget = _options.MaxPagesPerTick;
            var warmed = 0;
            var warmedPages = new HashSet<string>(StringComparer.Ordinal);
            // The BOOT pass runs its waves against the extended deadline
            // (BootPrewarmMaxDurationSeconds, default 180s): steady-state ticks cut at
            // MaxTickDurationMs (30s), which on a slow compose path (staging: 30-60s per
            // compose) left the 30-envelope set taking 20+ minutes. The boot pass lands
            // the whole pinned set in one pass. Background — never blocks boot.
            var deadline = _time.GetUtcNow()
                + (isBootPass
                    ? TimeSpan.FromSeconds(Math.Max(1, _options.BootPrewarmMaxDurationSeconds))
                    : TimeSpan.FromMilliseconds(_options.MaxTickDurationMs));
            var waveSize = Math.Max(1, _options.MaxConcurrentWarmsPerTick);

            // §7 Tier 3 (bounded parallelism): warm in waves of at most MaxConcurrentWarmsPerTick
            // concurrent composes, mirroring ScheduleCoordinator's own bounded-parallelism pattern.
            // MaxTickDurationMs is checked BETWEEN waves (not per item within a wave) -- count alone
            // doesn't bound cost when compose cost isn't uniform (a corpus-scale query regression),
            // so once elapsed exceeds the budget the remaining envelopes defer to the next tick
            // rather than grinding through the whole queue regardless of how slow composes have become.
            // One warm's cost measurement + failure sentinel (the wave body shared by the
            // main loop and the boot-pass retry round). The retry round passes a FRESH tick
            // (tick + 1): the atom caches the poison-guard Warming under (env, tick), so a
            // same-tick retry would return the cached Warming without re-composing.
            async Task<(string? PageKey, double CostMs)> WarmOneAsync(
                (DashboardPageManifest Manifest, DashboardPageWindow Window, bool IsPinned) item,
                long warmTick,
                CancellationToken ct2)
            {
                // Stage 2b: measures the REAL wall-clock cost of this one warm -- fed into
                // the adaptive controller as part of the tick's total measured cost, whether
                // the compose succeeded or threw (the time was spent either way).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var result = await WarmEnvelopeAsync(item.Manifest, item.Window, warmTick, ct2).ConfigureAwait(false);
                    // Per-envelope result visibility (2026-08-14, the traffic-windows-cold
                    // class: composes complete with 200s and real data yet never stamp — the
                    // wave's verdict per envelope was invisible). The Debug line names the
                    // exact result state for the failing envelopes: Warming vs real, and the
                    // slice presence (Summary/TimeBuckets) for the real ones.
                    _logger?.LogDebug(
                        "DashboardMaterializerCoordinator: warm result for {Page} tick {Tick}: {State} (summary={HasSummary}, timeBuckets={HasBuckets}, ms={CostMs:F0}).",
                        item.Manifest.PageKey, warmTick,
                        result.IsWarming ? "Warming" : "real",
                        result.Summary is not null, result.TimeBuckets is not null,
                        sw.Elapsed.TotalMilliseconds);
                    // A poison-guard Warming result IS a failure for the retry's purposes
                    // (the compose ran but returned no data) — the boot pass retries it in
                    // the same pass instead of letting the envelope ride the next tick.
                    return result.IsWarming
                        ? (null, sw.Elapsed.TotalMilliseconds)
                        : (item.Manifest.PageKey, sw.Elapsed.TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "DashboardMaterializerCoordinator: warm failed for {Page}.", item.Manifest.PageKey);
                    return (null, sw.Elapsed.TotalMilliseconds);
                }
            }

            var failedItems = new List<(DashboardPageManifest Manifest, DashboardPageWindow Window, bool IsPinned)>();

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
                        "DashboardMaterializerCoordinator: wave deadline exceeded after warming {Warmed}; {Deferred} envelope(s) deferred to next tick. (deadline = {DeadlineMs}ms — the boot pass uses BootPrewarmMaxDurationSeconds, steady-state ticks MaxTickDurationMs.)",
                        warmed, warmQueue.Count - start,
                        isBootPass ? _options.BootPrewarmMaxDurationSeconds * 1000 : _options.MaxTickDurationMs);
                    break;
                }

                ct.ThrowIfCancellationRequested();

                // Pinned coverage is deliberately serial in steady-state ticks: the
                // standard 7d/30d views are known to be the expensive cold reads, and
                // launching them alongside the short windows turns an idle-start prewarm
                // into a SQLite contention burst. EXCEPTION — the BOOT pass (operator
                // directive 2026-08-14): on the remote-mode host the compose is a gateway
                // round-trip, not a local scan, and a serial 30-envelope boot pass left
                // every non-default window spinning at first paint after a deploy. The
                // boot pass runs the pinned tier at BootPinnedWarmConcurrency; once the
                // pinned tier is complete, ordinary live envelopes still use the
                // configured bounded parallelism.
                var currentWaveSize = warmQueue[start].IsPinned
                    ? (isBootPass ? Math.Max(1, _options.BootPinnedWarmConcurrency) : 1)
                    : waveSize;
                var wave = warmQueue.Skip(start).Take(Math.Min(currentWaveSize, budget - warmed)).ToList();
                var waveResults = await Task.WhenAll(wave.Select(item => WarmOneAsync(item, tick, ct))).ConfigureAwait(false);

                for (var i = 0; i < waveResults.Length; i++)
                {
                    tickCostMs += waveResults[i].CostMs;
                    if (waveResults[i].PageKey is null)
                    {
                        failedItems.Add(wave[i]);
                        continue;
                    }
                    warmed++;
                    warmedPages.Add(waveResults[i].PageKey!);
                }

                start += wave.Count;
            }

            // Boot-pass failure retry (operator gate 2026-08-14: every standard window must
            // prerender at first paint on a fresh boot): a compose that failed at boot (the
            // gateway not yet ready, a transient timeout, or a gateway-side pressure window
            // — staging evidence 2026-08-14: the gateway shed writes under DB pressure and
            // the site's 10s compose timeouts died client-side for ~minutes) must not reset
            // the envelope to the next tick's cadence — D1 keeps failures un-stamped, so
            // they would re-warm at the next 10s tick, pushing the non-default windows'
            // first paint past 30s. Bounded retry rounds within the same pass (up to 3,
            // backoff 1s/2s/4s — spanning roughly the gateway's settling window), at the
            // bump concurrency, close the gap. Steady-state ticks never retry here (their
            // failures ride the cadence).
            var retrySize = Math.Max(1, _options.BootPinnedWarmConcurrency);
            for (var round = 1; isBootPass && failedItems.Count > 0 && round <= 3; round++)
            {
                _logger?.LogWarning(
                    "DashboardMaterializerCoordinator: boot pass had {Failed} failed envelope(s); retry round {Round}/3.",
                    failedItems.Count, round);
                await Task.Delay(TimeSpan.FromSeconds(round), ct).ConfigureAwait(false);
                var roundFailures = new List<(DashboardPageManifest Manifest, DashboardPageWindow Window, bool IsPinned)>();
                for (var start = 0; start < failedItems.Count;)
                {
                    var wave = failedItems.Skip(start).Take(Math.Min(retrySize, budget - warmed)).ToList();
                    var waveResults = await Task.WhenAll(wave.Select(item => WarmOneAsync(item, tick + round, ct))).ConfigureAwait(false);
                    for (var i = 0; i < waveResults.Length; i++)
                    {
                        tickCostMs += waveResults[i].CostMs;
                        if (waveResults[i].PageKey is null)
                        {
                            roundFailures.Add(wave[i]);
                            continue;
                        }
                        warmed++;
                        warmedPages.Add(waveResults[i].PageKey!);
                    }
                    start += wave.Count;
                }
                failedItems = roundFailures;
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
        catch (Exception ex)
        {
            // The silent-abort class (2026-08-14 staging: ticks advance, feedHistory 0,
            // 0/30 envelopes — the queue build throws before any warm, and the pass has
            // no catch, so the abort was invisible). The pass must never fail silently:
            // log at WARNING + record into the diagnostics so /internal/diagnostics/state
            // exposes the failure (tickFailures + lastTickFailure).
            _logger?.LogWarning(ex, "DashboardMaterializerCoordinator: materializer tick aborted before/during warm.");
            _diagnostics?.RecordTickFailure(ex);
        }
        finally
        {
            // Recorded exactly once per tick, regardless of which branch/early-return was
            // taken above -- including a tick where nothing was due (tickCostMs stays 0.0),
            // which is itself meaningful input: it lets the adaptive controller's smoothed
            // estimate relax back toward 1.0 during a genuinely quiet tick.
            _adaptive.RecordTickCost(tickCostMs);
            // The boot pass's bump ends when the pass that owns it completes.
            if (isBootPass)
                Interlocked.Exchange(ref _bootPassActive, 0);
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
