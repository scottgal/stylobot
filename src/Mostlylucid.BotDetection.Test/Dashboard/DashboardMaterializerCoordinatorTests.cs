using Microsoft.Extensions.Options;
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
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched);

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
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        // PrewarmDefaultEnvelope explicitly off here -- this test asserts the pure
        // demand-gate contract in isolation; the default-on prewarm behavior has its
        // own test below.
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false }), sched);

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
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched); // default: PrewarmDefaultEnvelope = true

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s); // nobody has ever viewed the page

        Assert.Equal(1, composes); // prewarmed anyway
    }

    [Fact]
    public async Task Disabled_coordinator_does_not_subscribe()
    {
        var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => 1L,
            Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => 1L), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { Enabled = false }), sched);

        await coord.StartAsync(default);
        Assert.Equal(0, sched.SubscriberCount);
    }

    [Fact]
    public async Task No_schedule_coordinator_is_a_safe_noop()
    {
        var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()), () => 1L,
            Options.Create(new DashboardMaterializerOptions()));
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => 1L), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), schedule: null);

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
