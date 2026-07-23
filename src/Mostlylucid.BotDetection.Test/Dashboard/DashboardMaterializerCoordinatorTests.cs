using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 5 of out-of-request materialization: the tick-driven coordinator warms
///     the content cache's live envelopes at the current tick, so the request path
///     reads a ready bundle instead of composing. Also asserts the viewer-mode /
///     disabled safety (self-disable without a schedule coordinator or when off).
/// </summary>
public sealed class DashboardMaterializerCoordinatorTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);
    private static DashboardPageResult Result() => new(new DashboardDatasetBundle(null, null, null, null, null));

    [Fact]
    public async Task Tick_warms_live_envelopes_ahead_of_reads()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()), sched);

        // A user read at tick 1 makes the envelope live and composes once.
        await cache.GetAsync(Traffic, Window(), tick, default);
        Assert.Equal(1, composes);

        await coord.StartAsync(default);
        tick = 2;                                    // tick advances
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        // The materializer warmed the live envelope at tick 2 (composed for the new tick).
        Assert.Equal(2, composes);

        // A user read at tick 2 now hits the warmed entry — no in-request compose.
        await cache.GetAsync(Traffic, Window(), 2, default);
        Assert.Equal(2, composes);

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task No_live_envelopes_means_no_compute_when_prewarm_is_off()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        // PrewarmDefaultEnvelope explicitly off here -- this test asserts the pure
        // demand-gate contract in isolation; the default-on prewarm behavior has its
        // own test below.
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false }), sched);

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s); // nobody viewed anything

        Assert.Equal(0, composes); // demand-gated: no viewers -> no work
    }

    [Fact]
    public async Task Tick_prewarms_default_page_even_with_zero_live_viewers()
    {
        // The gap this closes: without an unconditional prewarm, a visit after any idle
        // gap (fresh startup, or a quiet dashboard) always cold-misses because
        // LiveEnvelopes() is empty until a real request seeds it.
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()), sched); // default: PrewarmDefaultEnvelope = true

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s); // nobody has ever viewed the page

        Assert.Equal(1, composes); // prewarmed anyway
    }

    [Fact]
    public async Task Tick_stops_warming_once_the_time_budget_is_exceeded_even_with_pages_left_in_budget()
    {
        // Regression guard for the compose-batch-overload incident: a single tick's
        // sequential warm loop must not be allowed to run unbounded when individual
        // composes are slow (the query itself might momentarily degrade) -- it should
        // defer the rest to a later tick instead of monopolizing the DB back-to-back.
        // MaxPagesPerTick alone doesn't catch this: 3 pages is well under the default
        // budget of 32, but each "compose" here advances the clock past MaxTickDurationMs.
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var composed = new List<string>();
        long tick = 1;
        var cache = new DashboardContentCache((manifest, _, _) =>
            {
                composed.Add(manifest.PageKey);
                time.Advance(TimeSpan.FromMilliseconds(50)); // simulates one slow compose-batch call
                return Task.FromResult(Result());
            },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var pages = new[]
        {
            new DashboardPageManifest("dashboard.traffic", new[] { "summary" }),
            new DashboardPageManifest("dashboard.visitors", new[] { "summary" }),
            new DashboardPageManifest("dashboard.site", new[] { "summary" }),
        };
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                MaxTickDurationMs = 90, // budget check runs BEFORE each compose: page 1 (0ms elapsed) and
                                        // page 2 (50ms elapsed) both pass; after page 2 elapsed is 100ms,
                                        // so page 3's pre-compose check (100ms >= 90ms budget) defers it.
            }),
            sched, timeProvider: time);

        foreach (var page in pages)
            await cache.GetAsync(page, Window(), tick, default); // make each page live
        composed.Clear();

        await coord.StartAsync(default);
        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.True(composed.Count < pages.Length, "expected the time budget to defer at least one page");
        Assert.True(composed.Count > 0, "expected at least one page to be warmed before the budget was hit");
    }

    [Fact]
    public async Task Disabled_coordinator_still_subscribes_but_a_tick_does_no_work()
    {
        // Contract change: Enabled is now read live inside MaterializeTickAsync (not
        // gated at subscribe time) so an operator can flip it via config reload without a
        // restart -- the exact stabiliser this subsystem needed during the incident. The
        // coordinator therefore always subscribes when a schedule exists; disabled just
        // means every tick is a no-op.
        var composes = 0;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, () => 1L,
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => 1L), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions { Enabled = false }), sched);

        await coord.StartAsync(default);
        Assert.Equal(1, sched.SubscriberCount);

        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(0, composes);
    }

    [Fact]
    public async Task Tick_honors_a_live_config_flip_to_disabled_without_restart()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var monitor = new MutableOptionsMonitor<DashboardMaterializerOptions>(
            new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false });
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(), monitor, sched);

        await cache.GetAsync(Traffic, Window(), tick, default); // real read -> live envelope
        composes = 0;
        await coord.StartAsync(default);

        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(1, composes); // enabled: tick warms the live envelope

        monitor.CurrentValue.Enabled = false; // live flip (config reload), no restart
        tick = 3;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(1, composes); // disabled: the next tick does no work
    }

    [Fact]
    public async Task Tick_honors_a_live_config_flip_back_to_enabled_without_restart()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var monitor = new MutableOptionsMonitor<DashboardMaterializerOptions>(
            new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false, Enabled = false });
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(), monitor, sched);

        await cache.GetAsync(Traffic, Window(), tick, default);
        composes = 0;
        await coord.StartAsync(default);

        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(0, composes); // starts disabled: no work

        monitor.CurrentValue.Enabled = true; // live flip (config reload), no restart
        tick = 3;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(1, composes); // re-enabled: the next tick resumes warming
    }

    [Fact]
    public async Task No_schedule_coordinator_is_a_safe_noop()
    {
        var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => 1L,
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()));
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => 1L), new DefaultDashboardPageManifestSource(),
            new MutableOptionsMonitor<DashboardMaterializerOptions>(new DashboardMaterializerOptions()), schedule: null);

        await coord.StartAsync(default); // viewer-mode host: must not throw
        await coord.StopAsync(default);
    }

    // ---------------- fakes ----------------

    private sealed class FakeCursor : IDashboardChangeCursor
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
        public int SubscriberCount => _subs.Count;

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
