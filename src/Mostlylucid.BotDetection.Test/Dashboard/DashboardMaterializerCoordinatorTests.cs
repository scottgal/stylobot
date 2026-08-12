using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.UI.Dashboard;
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
        // Prewarm (Tier 1 pinned coverage) is orthogonal to this test's concern -- it has its
        // own dedicated test below -- so it's off here to isolate the demand-gated (Tier 2) path.
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false }), sched);

        // A user read at tick 1 makes the envelope live but does NOT compose (structural
        // §8 fix -- the request path never computes; it's a genuine cold miss/placeholder).
        await cache.GetAsync(Traffic, Window(), tick, default);
        Assert.Equal(0, composes);

        await coord.StartAsync(default);
        tick = 2;                                    // tick advances
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        // The materializer warmed the live envelope at tick 2 (the only compose).
        Assert.Equal(1, composes);

        // A user read at tick 2 now hits the warmed entry — no additional compose.
        await cache.GetAsync(Traffic, Window(), 2, default);
        Assert.Equal(1, composes);

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

        // §7 Tier 1 + a490698a + 3a345664: pinned prewarm now covers all six top-level page
        // manifests (traffic/topbots/clusters/sessions/threats/site) × 4 PrewarmWindows = 24 composes.
        Assert.Equal(24, composes);
    }

    [Fact]
    public async Task Tick_prewarms_every_configured_window_token_at_the_default_page()
    {
        var composedWindows = new List<DashboardPageWindow>();
        long tick = 1;
        var cache = new DashboardContentCache((_, window, _) => { composedWindows.Add(window); return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched);

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        // a490698a + 3a345664: all six page manifests × 4 window tokens = 24 composes. Each
        // manifest gets the same bucket sizes, so the sorted list has 6 of each bucket-minute value.
        var bucketMinutes = composedWindows.Select(w => w.BucketMinutes).OrderBy(m => m).ToArray();
        Assert.Equal(24, bucketMinutes.Length);
        var expected = new[] { 5, 20, 120, 480 };
        foreach (var m in expected)
            Assert.Equal(6, bucketMinutes.Count(bm => bm == m));
    }

    [Fact]
    public async Task Tick_prewarms_standard_7d_and_30d_windows_as_cache_hits()
    {
        // Regression lock for the cold-window P0: the pinned windows must use the
        // same bucket-normalized envelope that a top-level Traffic read uses. A
        // completed startup/background pass therefore serves 7d/30d without a
        // request-thread compose or a false-empty Warming result.
        var now = new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTimeProvider(now);
        long tick = 1;
        var composes = 0;
        await using var cache = new DashboardContentCache((_, _, _) =>
            {
                composes++;
                return Task.FromResult(Result());
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched, timeProvider: time);

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        var traffic = new DefaultDashboardPageManifestSource().For("dashboard.traffic")!;

        foreach (var (token, minutes) in new[] { ("7d", 7 * 24 * 60), ("30d", 30 * 24 * 60) })
        {
            var window = new DashboardPageWindow(
                StartTime: now.UtcDateTime.AddMinutes(-minutes),
                EndTime: now.UtcDateTime,
                AudienceFilter: "all",
                ProbMin: null,
                Domains: null,
                TopN: 500,
                BucketMinutes: (int)HitsPerPeriodChartletBuilder.BucketSizeForWindow(token).TotalMinutes);

            var result = await cache.GetCurrentAsync(traffic, window, default);
            Assert.False(result.IsWarming, $"Expected prewarmed {token} Traffic envelope to be a cache hit.");
        }

        Assert.Equal(24, composes); // all six page manifests × 4 windows warm; reads never compose.
        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Tick_serializes_pinned_standard_window_warms_even_when_live_wave_parallelism_is_four()
    {
        // 7d/30d cold materialization is expensive. A four-way pinned startup wave
        // caused those corpus scans to contend with each other and time out; the
        // pinned tier must remain serial while live envelopes may still be parallel.
        var sync = new object();
        var inFlight = 0;
        var maxObserved = 0;
        long tick = 1;
        await using var cache = new DashboardContentCache(async (_, _, _) =>
            {
                lock (sync)
                {
                    inFlight++;
                    maxObserved = Math.Max(maxObserved, inFlight);
                }
                await Task.Delay(15);
                lock (sync) inFlight--;
                return Result();
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { MaxConcurrentWarmsPerTick = 4 }), sched);

        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.Equal(1, maxObserved);
        await coord.StopAsync(default);
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
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var pages = new[]
        {
            new DashboardPageManifest("dashboard.traffic", new[] { "summary" }),
            new DashboardPageManifest("dashboard.visitors", new[] { "summary" }),
            new DashboardPageManifest("dashboard.site", new[] { "summary" }),
        };
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                MaxTickDurationMs = 90, // budget check runs BEFORE each wave: page 1 (0ms elapsed) and
                                        // page 2 (50ms elapsed) both pass; after page 2 elapsed is 100ms,
                                        // so page 3's pre-wave check (100ms >= 90ms budget) defers it.
                MaxConcurrentWarmsPerTick = 1, // one page per wave -- isolates the per-item deadline
                                                // granularity this test asserts; §7 Tier 3's own wave-
                                                // concurrency behavior has its own dedicated test.
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
    public async Task Tick_warms_the_hotter_live_envelope_first_when_the_budget_cant_cover_both()
    {
        // §7 Tier 2, single-source-corrected: ranking comes from SlidingCacheAtom's OWN
        // AccessCount (via DashboardContentCache.LiveEnvelopes() -> TryGetEntryStats), not a
        // parasitic counter. Under budget pressure, the envelope actually hit more should win.
        var composedPages = new List<string>();
        long tick = 1;
        var cache = new DashboardContentCache((manifest, _, _) => { composedPages.Add(manifest.PageKey); return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var cool = new DashboardPageManifest("dashboard.site", new[] { "summary" });
        var hot = new DashboardPageManifest("dashboard.visitors", new[] { "summary" });

        // GetAsync alone no longer creates an atom entry (structural §8 fix — the request
        // path never composes), so WarmAsync seeds the entry and GetAsync marks liveness +
        // bumps AccessCount via a genuine hit.
        await cache.WarmAsync(cool, Window(), tick, default); // AccessCount 1 (created)
        await cache.GetAsync(cool, Window(), tick, default);  // live + AccessCount 2

        await cache.WarmAsync(hot, Window(), tick, default);  // AccessCount 1 (created)
        await cache.GetAsync(hot, Window(), tick, default);   // live + AccessCount 2
        await cache.GetAsync(hot, Window(), tick, default);   // AccessCount 3 -- now the hotter envelope
        composedPages.Clear();

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                MaxPagesPerTick = 1, // only room for one of the two live envelopes
            }),
            sched);

        tick = 2;
        await coord.StartAsync(default);
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.Equal(new[] { hot.PageKey }, composedPages);
    }

    [Fact]
    public async Task Tick_warms_a_wave_of_envelopes_concurrently_up_to_MaxConcurrentWarmsPerTick()
    {
        // §7 Tier 3: MaxConcurrentWarmsPerTick=2 with 2 live envelopes should run BOTH
        // composes concurrently (a single wave), not serialize them. Each compose blocks
        // (once armed) until released, so if the coordinator ran them sequentially, only
        // one would ever be in flight at a time and maxObserved would stay at 1.
        var sync = new object();
        var inFlight = 0;
        var maxObserved = 0;
        var block = false;
        var gate = new TaskCompletionSource();

        long tick = 1;
        var cache = new DashboardContentCache(async (_, _, _) =>
            {
                if (block)
                {
                    lock (sync) { inFlight++; maxObserved = Math.Max(maxObserved, inFlight); }
                    await gate.Task;
                    lock (sync) { inFlight--; }
                }
                return Result();
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        var a = new DashboardPageManifest("dashboard.a", new[] { "summary" });
        var b = new DashboardPageManifest("dashboard.b", new[] { "summary" });
        await cache.GetAsync(a, Window(), tick, default); // seed liveness -- non-blocking (block=false)
        await cache.GetAsync(b, Window(), tick, default);

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                MaxConcurrentWarmsPerTick = 2,
            }),
            sched);

        await coord.StartAsync(default);
        tick = 2;
        block = true;
        var tickTask = sched.RaiseTickAsync(TickCadence.Tick10s);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Volatile.Read(ref maxObserved) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        gate.TrySetResult();
        await tickTask;

        Assert.Equal(2, maxObserved);
    }

    [Fact]
    public async Task Disabled_coordinator_still_subscribes_but_a_tick_does_no_work()
    {
        // Enabled is a startup snapshot (FOSS hard rule: no runtime options-reload) checked
        // inside MaterializeTickAsync rather than gating the subscription in StartAsync -- a
        // single code path, even though there's no live flip to benefit from it. The
        // coordinator always subscribes when a schedule exists; disabled just means every
        // tick is a no-op for the life of the process.
        var composes = 0;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); }, () => 1L,
            Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => 1L), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { Enabled = false }), sched);

        await coord.StartAsync(default);
        Assert.Equal(1, sched.SubscriberCount);

        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(0, composes);
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

    [Fact]
    public async Task HasWarmedSuccessfully_starts_false_becomes_true_after_first_tick()
    {
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()),
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched);

        // Before start: has never warmed
        Assert.False(coord.HasWarmedSuccessfully);

        await coord.StartAsync(default);
        // Make an envelope live so it gets warmed
        await cache.GetAsync(Traffic, Window(), tick, default);
        tick++;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        // After first successful tick: warmed
        Assert.True(coord.HasWarmedSuccessfully);

        await coord.StopAsync(default);
    }

    [Fact]
    public async Task HasWarmedSuccessfully_stays_false_when_tick_fails()
    {
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => Task.FromException<DashboardPageResult>(new InvalidOperationException("fail")),
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()), sched);

        await coord.StartAsync(default);
        Assert.False(coord.HasWarmedSuccessfully);

        // Make an envelope live, then fire a tick — the compose throws
        await cache.GetAsync(Traffic, Window(), tick, default);
        tick++;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        // After a failed tick: still false
        Assert.False(coord.HasWarmedSuccessfully);

        await coord.StopAsync(default);
    }
}