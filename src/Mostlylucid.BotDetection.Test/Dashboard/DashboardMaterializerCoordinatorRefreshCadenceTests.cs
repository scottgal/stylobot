using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2b: TDD coverage for the per-page-key refresh cadence wired into
///     <see cref="DashboardMaterializerCoordinator.MaterializeTickAsync"/> --
///     a live/pinned envelope is only actually re-warmed once its
///     <see cref="DashboardRefreshCadence.ComputeEffectiveIntervalSeconds"/> has
///     genuinely elapsed since its last real warm, not on every Tick10s. See
///     <see cref="DashboardRefreshCadenceTests"/> for the pure function's own unit
///     coverage (including the named MIN-invariant test) and
///     <see cref="DashboardMaterializerAdaptiveControllerTests"/> for the adaptive
///     scale factor this wiring feeds/consumes.
/// </summary>
public sealed class DashboardMaterializerCoordinatorRefreshCadenceTests
{
    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);
    private static DashboardPageResult Result() => new(new DashboardDatasetBundle(null, null, null, null, null));

    private static DashboardMaterializerOptions MaterializerOptions(int globalMin = 60, int aggregateBase = 300, int liveBase = 60) => new()
    {
        PrewarmDefaultEnvelope = false,
        GlobalMinIntervalSeconds = globalMin,
        AggregateBaseIntervalSeconds = aggregateBase,
        LiveBaseIntervalSeconds = liveBase,
    };

    [Fact]
    public async Task Aggregate_only_key_is_skipped_on_a_tick_before_its_base_interval_elapses()
    {
        var composes = 0;
        long tick = 1;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(MaterializerOptions()), sched, timeProvider: time);

        await cache.GetAsync(manifest, Window(), tick, default); // make it live, no compose yet
        await coord.StartAsync(default);

        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s); // never warmed before -> due -> warms
        Assert.Equal(1, composes);

        // Well under the 300s Aggregate base interval -- must NOT re-warm.
        time.Advance(TimeSpan.FromSeconds(10));
        tick = 3;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(1, composes);

        // Past the 300s Aggregate base interval -- must re-warm.
        time.Advance(TimeSpan.FromSeconds(300));
        tick = 4;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(2, composes);

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Shared_traffic_shaped_key_still_refreshes_at_the_Live_cadence_while_a_pure_Aggregate_key_does_not()
    {
        // The coordinator-level proof of THE NAMED INVARIANT: dashboard.traffic's real
        // widget-key shape (mostly Aggregate + the Live "top-bots" widget) must keep
        // refreshing at the FAST (Live) cadence, never falling back to the slow Aggregate
        // one just because most of its widgets are Aggregate-tolerant.
        var trafficComposes = 0;
        var clustersComposes = 0;
        long tick = 1;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new DashboardContentCache((manifest, _, _) =>
            {
                if (manifest.PageKey == "dashboard.traffic") trafficComposes++;
                else clustersComposes++;
                return Task.FromResult(Result());
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        var trafficManifest = new DashboardPageManifest(
            "dashboard.traffic",
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health" });
        var clustersManifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(MaterializerOptions()), sched, timeProvider: time);

        await cache.GetAsync(trafficManifest, Window(), tick, default);
        await cache.GetAsync(clustersManifest, Window(), tick, default);
        await coord.StartAsync(default);

        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s); // first-ever warm for both
        Assert.Equal(1, trafficComposes);
        Assert.Equal(1, clustersComposes);

        // 70s: past the 60s Live base interval, but well under the 300s Aggregate one.
        time.Advance(TimeSpan.FromSeconds(70));
        tick = 3;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.Equal(2, trafficComposes); // Live-class widget forces the fast cadence.
        Assert.Equal(1, clustersComposes); // pure Aggregate key is correctly still not due.

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Never_before_warmed_envelope_is_always_due_on_its_first_tick()
    {
        // Guards the "first-ever warm always happens" contract every pre-existing
        // single-tick coordinator test relies on implicitly.
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(MaterializerOptions()), sched);

        await cache.GetAsync(manifest, Window(), tick, default);
        await coord.StartAsync(default);
        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.Equal(1, composes);
    }

    [Fact]
    public async Task Pinned_prewarm_envelope_is_also_subject_to_the_due_time_gate()
    {
        var composedWindows = new List<DashboardPageWindow>();
        long tick = 1;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new DashboardContentCache((_, window, _) => { composedWindows.Add(window); return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        // PrewarmDefaultEnvelope stays on (default) here -- unlike the Options() helper above.
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                GlobalMinIntervalSeconds = 60,
                AggregateBaseIntervalSeconds = 300,
                LiveBaseIntervalSeconds = 60,
            }),
            sched, timeProvider: time);

        await coord.StartAsync(default);
        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s); // first-ever: all 24 pinned envelopes warm
        Assert.Equal(24, composedWindows.Count);

        composedWindows.Clear();
        // dashboard.traffic touches the Live-class "top-bots" widget -> 60s cadence. 10s
        // later is well under that, so nothing should re-warm yet.
        time.Advance(TimeSpan.FromSeconds(10));
        tick = 3;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Empty(composedWindows);

        // Past 60s -- the Live-class envelopes are due again; Aggregate-class
        // (300s cadence: clusters + site) are not, so only 16/24 re-warm this tick.
        time.Advance(TimeSpan.FromSeconds(60));
        tick = 4;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(16, composedWindows.Count);

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Adaptive_scale_factor_rises_after_repeated_slow_ticks_and_relaxes_after_fast_ones()
    {
        // Real (Task.Delay-based) slowness -- the coordinator measures REAL wall-clock cost
        // via Stopwatch, so a fake-clock advance inside the factory would not register.
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        long tick = 1;
        var slow = true;
        var cache = new DashboardContentCache(async (_, _, _) =>
            {
                // 400ms, not 100ms: under a full parallel test-suite run (thread-pool
                // contention across thousands of concurrent tests) a tight margin over the
                // 10ms budget can measure as not-slow-enough if the surrounding await
                // machinery itself picks up scheduling jitter. This is a margin fix against
                // contention, not a correctness change to the coordinator's cost logic.
                if (slow) await Task.Delay(400);
                return Result();
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                GlobalMinIntervalSeconds = 1, // tiny -- keeps the envelope due well within the 10s we advance below
                AggregateBaseIntervalSeconds = 1,
                LiveBaseIntervalSeconds = 1,
                RefreshCostBudgetMs = 10, // a single 100ms compose exceeds this by 10x
                AdaptiveCostSmoothingAlpha = 1.0, // isolate escalation/relaxation, no smoothing lag
            }),
            sched, timeProvider: time);

        await cache.GetAsync(manifest, Window(), tick, default);
        await coord.StartAsync(default);

        Assert.Equal(1.0, coord.CurrentAdaptiveScaleFactor);

        // Run enough slow ticks to guarantee the EMA pushes the smoothed cost well past
        // the RefreshCostBudgetMs threshold. With alpha=1.0 the smoothed cost jumps
        // directly to 100ms after the first warm tick, but the coordinator may skip
        // warming if the envelope isn't due yet -- so give it 6 ticks (60s of fake time)
        // to absorb any scheduling jitter.
        // With AdaptiveCostSmoothingAlpha=1.0 (chosen deliberately to isolate escalation with
        // no smoothing lag) the scale factor OSCILLATES rather than staying escalated: a slow
        // tick pushes it up (e.g. ~41x for a 400ms compose over a 10ms budget), but
        // IsDueForWarm's interval computation itself scales BY that same factor -- so the next
        // tick's required interval balloons past what 10s of fake-time-advance satisfies, the
        // envelope is skipped, and RecordTickCost(0) (documented, intentional: "a genuinely
        // quiet tick" relaxes the estimate) collapses it straight back to 1.0. Repeats every
        // other tick. This is real, by-design coordinator behavior, not a test bug -- a single
        // point-in-time read after a fixed tick count can land on either phase of the
        // oscillation. Track the max observed across the slow phase instead: that proves
        // escalation genuinely happens without asserting it stays pinned, which the documented
        // relax-on-quiet-tick behavior never promises.
        var maxScale = coord.CurrentAdaptiveScaleFactor;
        for (var i = 0; i < 6; i++)
        {
            tick++;
            await sched.RaiseTickAsync(TickCadence.Tick10s);
            time.Advance(TimeSpan.FromSeconds(10));
            maxScale = Math.Max(maxScale, coord.CurrentAdaptiveScaleFactor);
        }

        Assert.True(maxScale > 1.0, $"repeated over-budget compose cost should have raised the scale factor at some point; max={maxScale}");

        // Now go fast: the compose returns immediately, so measured cost drops to ~0.
        slow = false;
        for (var i = 0; i < 8; i++)
        {
            tick++;
            await sched.RaiseTickAsync(TickCadence.Tick10s);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.True(coord.CurrentAdaptiveScaleFactor < maxScale, $"fast ticks should relax the scale factor back down; peak={maxScale}, current={coord.CurrentAdaptiveScaleFactor}");

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Repeated_runs_MarkDirty_races_the_tick_loop_and_still_computes_only_once()
    {
        // Re-verifies the Stage 2a concurrency guard (DashboardMaterializerCoordinatorMarkDirtyTests)
        // still holds now that AwaitWarmAndClearAsync also writes _lastWarmedAt -- run several
        // times in one test body to rule out a timing-dependent flake the way the branch's
        // prior commit found one for the original in-flight-warm guard.
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var gate = new TaskCompletionSource();
            var composeStarted = new TaskCompletionSource();
            var composeCount = 0;
            long tick = 1;
            var cache = new DashboardContentCache(async (_, _, _) =>
                {
                    Interlocked.Increment(ref composeCount);
                    composeStarted.TrySetResult();
                    await gate.Task;
                    return Result();
                },
                () => tick, Options.Create(new DashboardMaterializerOptions()));
            var manifest = new DashboardPageManifest("dashboard.traffic", new[] { "summary" });
            var sched = new FakeScheduleCoordinator();
            var coord = new DashboardMaterializerCoordinator(
                cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
                Options.Create(new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false }),
                sched);

            await cache.GetAsync(manifest, Window(), tick, default);
            await coord.StartAsync(default);

            var tickTask = sched.RaiseTickAsync(TickCadence.Tick10s);
            await composeStarted.Task;

            var markDirtyTask = coord.MarkDirtyAsync(manifest.PageKey);

            gate.TrySetResult();
            await Task.WhenAll(tickTask, markDirtyTask);

            Assert.Equal(1, composeCount);
            await coord.StopAsync(default);
        }
    }

    // ---------------- fakes ----------------

    private sealed class FakeCursor : Mostlylucid.BotDetection.UI.Services.IDashboardChangeCursor
    {
        private readonly Func<long> _tick;
        public FakeCursor(Func<long> tick) => _tick = tick;
        public long CurrentTick => _tick();
        public void Bump(string surface) { }
        public long TickFor(string surface) => 0;
        public IReadOnlyList<string> SurfacesChangedThisTick() => Array.Empty<string>();
    }

    private sealed class FakeScheduleCoordinator : IScheduleCoordinator
    {
        private readonly List<(TickCadence Cadence, Func<DateTimeOffset, CancellationToken, Task> Handler)> _subs = new();

        public IDisposable Subscribe(TickCadence cadence, string subscriberName, CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            _subs.Add((cadence, handler));
            return new Sub(() => _subs.RemoveAll(s => s.Handler == handler));
        }

        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();

        public async Task RaiseTickAsync(TickCadence cadence)
        {
            foreach (var s in _subs.Where(x => x.Cadence == cadence).ToList())
                await s.Handler(DateTimeOffset.UtcNow, CancellationToken.None);
        }

        private sealed class Sub : IDisposable
        {
            private readonly Action _onDispose;
            public Sub(Action onDispose) => _onDispose = onDispose;
            public void Dispose() => _onDispose();
        }
    }
}
