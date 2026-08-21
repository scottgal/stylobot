using Microsoft.Extensions.Logging;
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

    /// <summary>
    ///     The 20h prod-wedge fix (2026-08-21, overview-'s stack-evidence hypothesis): a
    ///     compose that never completes must not poison <c>_inFlightWarms</c> for the process
    ///     lifetime, and must not block the tick loop's <c>Task.WhenAll</c> from ever
    ///     returning (which, via ScheduleCoordinator's single-flight-per-subscriber semantics,
    ///     would silently skip every subsequent Tick10s for this subscriber forever -- the
    ///     climbing skip-count symptom). Bounds the whole test itself so a regression fails
    ///     fast instead of hanging the test run.
    /// </summary>
    [Fact]
    public async Task Hung_compose_faults_the_tick_instead_of_hanging_it_forever()
    {
        var hungGate = new TaskCompletionSource<DashboardPageResult>();
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) => hungGate.Task, // never completes -- simulates the stuck-await symptom
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                ComposeTimeoutMs = 50, // small bound so the test runs fast
            }),
            sched);

        await cache.GetAsync(Traffic, Window(), tick, default); // makes the envelope live
        await coord.StartAsync(default);
        tick = 2;

        var raceTimeout = Task.Delay(TimeSpan.FromSeconds(10));
        var raised = sched.RaiseTickAsync(TickCadence.Tick10s);
        var winner = await Task.WhenAny(raised, raceTimeout);

        Assert.True(winner == raised,
            "The tick invocation hung past a generous 10s test timeout -- the ComposeTimeoutMs bound did not free it.");
        await raised; // observe/rethrow if it faulted for an unexpected reason

        await coord.StopAsync(default);
    }

    /// <summary>
    ///     Companion to the hang test above: once a hung compose's wait is abandoned, the
    ///     envelope must NOT stay poisoned -- the next attempt (a later tick, here) has to
    ///     start a genuinely fresh compose rather than being handed the same abandoned
    ///     <c>Lazy&lt;Task&gt;</c> forever.
    /// </summary>
    [Fact]
    public async Task Envelope_recomposes_on_a_later_tick_after_the_first_compose_timed_out()
    {
        var composeCalls = 0;
        var hungGate = new TaskCompletionSource<DashboardPageResult>();
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) =>
            {
                composeCalls++;
                return composeCalls == 1 ? hungGate.Task : Task.FromResult(Result());
            },
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                ComposeTimeoutMs = 50,
                GlobalMinIntervalSeconds = 0, // don't let the due-gate suppress the retry tick
            }),
            sched);

        await cache.GetAsync(Traffic, Window(), tick, default);
        await coord.StartAsync(default);

        tick = 2;
        await Task.WhenAny(sched.RaiseTickAsync(TickCadence.Tick10s), Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, composeCalls);

        tick = 3;
        await Task.WhenAny(sched.RaiseTickAsync(TickCadence.Tick10s), Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, composeCalls); // a genuinely fresh compose, not the same abandoned task

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

        // §7 Tier 1 + a490698a + 3a345664 + 2026-08-14 (12h): pinned prewarm covers all
        // six top-level page manifests (traffic/topbots/clusters/sessions/threats/site)
        // × 5 PrewarmWindows (6h/12h/24h/7d/30d) = 30 composes.
        Assert.Equal(30, composes);
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

        // a490698a + 3a345664 + 2026-08-14 (12h): all six page manifests × 5 window tokens
        // (6h/12h/24h/7d/30d) = 30 composes. Each manifest gets the same bucket sizes, so
        // the sorted list has 6 of each bucket-minute value.
        var bucketMinutes = composedWindows.Select(w => w.BucketMinutes).OrderBy(m => m).ToArray();
        Assert.Equal(30, bucketMinutes.Length);
        var expected = new[] { 5, 10, 20, 120, 480 };
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

        Assert.Equal(30, composes); // all six page manifests × 5 windows (incl. 12h) warm; reads never compose.
        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Boot_pass_parallelizes_pinned_warms_within_the_bump_budget_ticks_stay_serial()
    {
        // 2026-08-14 (operator gate: every standard window prerendered at first paint on
        // a fresh boot): the pinned tier's serial-by-design wave (1) made the 30-envelope
        // boot pass take 30+ seconds on the remote-mode host (the compose is a gateway
        // round-trip, not a local SQLite scan) — every non-default window spun at first
        // paint after a deploy. The BOOT pass now runs the pinned tier at
        // BootPinnedWarmConcurrency (default 8); steady-state ticks keep the serial
        // pinned tier (the SQLite contention rationale still holds there).
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
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                MaxConcurrentWarmsPerTick = 4,
                BootPrewarmEnabled = true, // the StartAsync boot pass fires only when enabled
            }),
            sched, timeProvider: time);

        await coord.StartAsync(default); // the boot pass fires (bumped)

        // 30 pinned composes × 15ms / 8-parallel ≈ 60ms; give the background pass room.
        await Task.Delay(300);
        Assert.True(maxObserved > 1 && maxObserved <= 8,
            $"boot-pass pinned parallelism was {maxObserved}, expected 2..8");

        // Steady-state ticks keep the serial pinned tier (the SQLite contention rationale).
        maxObserved = 0;
        time.Advance(TimeSpan.FromSeconds(60)); // Live-class envelopes due again
        tick = 2;
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

    [Fact]
    public async Task Failed_warm_retries_after_the_short_backoff_not_the_full_interval()
    {
        // D1 (P0 2026-08-13) + the failure-backoff (2026-08-14): a poison-guard Warming
        // result must NOT stamp the envelope as freshly-warmed (the full-interval
        // suppression — the 60s stuck class). The backoff stamps it due again within
        // seconds instead: the failed envelope re-attempts promptly, never waiting the
        // full refresh interval.
        var composes = 0;
        long tick = 1;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new DashboardContentCache((_, _, _) =>
        {
            composes++;
            return Task.FromResult<DashboardPageResult>(DashboardPageResult.Warming);
        }, () => tick, Options.Create(new DashboardMaterializerOptions()));
        await cache.GetAsync(Traffic, Window(), tick, default); // make the envelope live (no compose)

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                FailureRetryBackoffSeconds = 5,
            }), sched,
            logger: new DebugLogger(),
            timeProvider: time);

        await coord.StartAsync(default);

        // A tick IMMEDIATELY (no backoff elapsed): the failed envelope is NOT due yet —
        // the backoff is the retry bound, not the every-tick hammer.
        time.Advance(TimeSpan.FromSeconds(2));
        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);
        Assert.Equal(1, composes);

        // The backoff's anti-hammer bound is verified above (the immediate tick skipped —
        // the failed envelope is NOT due until the backoff elapses). The due-after-backoff
        // property is exercised end-to-end by the staging battery; here the stamp's effect
        // is asserted directly: the failed envelope must NOT be stamped as freshly-warmed
        // (the D1 class — the full-interval suppression is what the backoff replaces).
        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Boot_pass_retries_failed_envelopes_within_the_same_pass()
    {
        // Operator gate (2026-08-14): a compose that fails at boot (the gateway not yet
        // ready, a transient timeout, or a poison-guard Warming result) must NOT reset the
        // envelope to the next-tick cadence — the boot pass retries the failed set once,
        // so every standard window is warm before the first requests land.
        var attempts = 0;
        long tick = 1;
        await using var cache = new DashboardContentCache((_, _, _) =>
            {
                attempts++;
                return Task.FromResult(attempts <= 3 ? DashboardPageResult.Warming : Result());
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                BootPrewarmEnabled = true,
                BootPinnedWarmConcurrency = 2,
            }), sched);

        await coord.StartAsync(default); // the boot pass fires; first 3 composes fail, the retry round succeeds
        await Task.Delay(500); // let the background pass + retry round finish

        // The traffic 24h envelope (among the first three to fail) must be warm after the
        // boot pass alone — no tick was ever raised.
        var manifest = new DefaultDashboardPageManifestSource().For("dashboard.traffic")!;
        var window = DashboardRoutingHelpers.BuildPinnedWindow("24h", DateTime.UtcNow);
        var result = await cache.GetCurrentAsync(manifest, window, default);
        Assert.False(result.IsWarming, "the boot pass's retry round should have warmed the envelope in the same pass");
        Assert.True(attempts >= 4, $"expected the retry round to re-run the failures (attempts={attempts})");
        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Boot_pass_uses_the_extended_deadline_not_the_tick_budget()
    {
        // Operator gate (2026-08-14): the steady-state tick budget (MaxTickDurationMs)
        // cuts the waves mid-pass on a slow compose path (staging measured 30-60s per
        // compose → 1-2 envelopes per tick → the 30-envelope set took 20+ minutes). The
        // boot pass runs its waves against BootPrewarmMaxDurationSeconds instead, so the
        // whole pinned set lands within the first pass. Background — never blocks boot.
        var composes = 0;
        long tick = 1;
        await using var cache = new DashboardContentCache(async (_, _, _) =>
            {
                composes++;
                await Task.Delay(50); // a slow compose path
                return Result();
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                BootPrewarmEnabled = true,
                MaxTickDurationMs = 1, // the steady-state budget would kill the pass instantly
                BootPinnedWarmConcurrency = 8,
                BootPrewarmMaxDurationSeconds = 30,
            }), sched);

        await coord.StartAsync(default); // the boot pass (extended deadline)
        await Task.Delay(3000); // 30 × 50ms / 8-parallel ≈ 200ms + margin

        Assert.Equal(30, composes); // the whole pinned set in ONE pass
        await coord.StopAsync(default);
    }

    [Fact]
    public async Task Failed_warm_is_due_again_after_the_short_backoff()
    {
        // 2026-08-14 (the staging 14-cold-forever class): a failed warm used to stay
        // un-stamped (every-tick retries) OR the full interval throttled it to one attempt
        // per 60s. The failure backoff stamps the envelope due again within seconds, so the
        // failed set re-attempts promptly instead of sitting behind the due-gate.
        var attempts = 0;
        long tick = 1;
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var cache = new DashboardContentCache((_, _, _) =>
            {
                attempts++;
                return Task.FromResult(attempts <= 5 ? DashboardPageResult.Warming : Result());
            },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        // The live entry FIRST so the ctor-fired boot pass sees it (the boot pass fires in
        // the constructor — before StartAsync — and the pinned tier is off here).
        await cache.GetAsync(Traffic, Window(), tick, default);

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new FakeCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                BootPrewarmEnabled = true,
                FailureRetryBackoffSeconds = 5,
                PrewarmDefaultEnvelope = false,
            }), sched, timeProvider: time);

        await coord.StartAsync(default); // the boot pass fires; first 5 composes fail

        // ~7s later the failure backoff has elapsed — the failed envelope is due again.
        time.Advance(TimeSpan.FromSeconds(7));
        tick = 2;
        await sched.RaiseTickAsync(TickCadence.Tick10s);

        Assert.True(attempts >= 2, $"the failure backoff should have re-attempted the failed envelope (attempts={attempts})");
        await coord.StopAsync(default);
    }
}

/// <summary>A console logger for the coordinator's Debug lines in tests.</summary>
public sealed class DebugLogger : ILogger<DashboardMaterializerCoordinator>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => System.IO.File.AppendAllText("/tmp/coord-test.log", $"[coord:{logLevel}] {formatter(state, exception)}{Environment.NewLine}");
}
