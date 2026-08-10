using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     TDD coverage for <see cref="DashboardMaterializerCoordinator.MarkDirtyAsync"/> --
///     the out-of-band hook a BACKGROUND caller (e.g. a SignalR relay reacting to an
///     upstream gateway's "data changed" push, never a request thread) uses to force an
///     immediate re-warm of one already-live page, bypassing the normal Tick10s schedule.
///
///     <para>
///         "Live" reuses <see cref="IDashboardContentCache.LiveEnvelopes"/> -- the exact
///         registry the tick loop already ranks against -- so a page nobody is currently
///         viewing is a safe no-op rather than a forced compute (preserving the
///         LFU-demand-gating principle Stage 2a's content cache relies on).
///     </para>
/// </summary>
public sealed class DashboardMaterializerCoordinatorMarkDirtyTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);
    private static DashboardPageResult Result() => new(new DashboardDatasetBundle(null, null, null, null, null));

    [Fact]
    public async Task Page_key_with_no_live_envelope_is_a_safe_noop()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var coord = new DashboardMaterializerCoordinator(
            cache, new RecordingCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()));

        // Nobody has ever read "dashboard.traffic" -- LiveEnvelopes() is empty for it.
        var triggered = await coord.MarkDirtyAsync(Traffic.PageKey);

        Assert.False(triggered);
        Assert.Equal(0, composes);
    }

    [Fact]
    public async Task Live_page_key_is_warmed_immediately_outside_any_tick()
    {
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var coord = new DashboardMaterializerCoordinator(
            cache, new RecordingCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()));

        // A prior read makes the envelope live (no compose yet -- structural §8 fix).
        await cache.GetAsync(Traffic, Window(), tick, default);
        Assert.Equal(0, composes);

        // No tick has fired. MarkDirtyAsync alone must trigger the compose.
        var triggered = await coord.MarkDirtyAsync(Traffic.PageKey);

        Assert.True(triggered);
        Assert.Equal(1, composes);
    }

    [Fact]
    public async Task Disabled_coordinator_treats_MarkDirtyAsync_as_a_noop_too()
    {
        // Enabled is the master switch checked by the tick path (MaterializeTickAsync);
        // MarkDirtyAsync must respect the same startup-snapshot gate rather than provide
        // a back door around it.
        var composes = 0;
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => { composes++; return Task.FromResult(Result()); },
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var coord = new DashboardMaterializerCoordinator(
            cache, new RecordingCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { Enabled = false }));

        await cache.GetAsync(Traffic, Window(), tick, default);

        var triggered = await coord.MarkDirtyAsync(Traffic.PageKey);

        Assert.False(triggered);
        Assert.Equal(0, composes);
    }

    [Fact]
    public async Task MarkDirtyAsync_bumps_the_cursor_and_broadcasts_the_same_way_a_tick_warm_does()
    {
        // Mirrors MaterializeTickAsync's own gate exactly: the cursor bump + SignalR queue
        // only fire when a hub context is wired (the tick loop has the identical
        // "warmedPages.Count > 0 && _hubContext is not null" gate), so this test wires a
        // hub context double to observe both downstream effects MarkDirtyAsync must reuse.
        var hub = new BroadcastDirtyTests.CapturingHub();
        var hubContext = new BroadcastDirtyTests.CapturingHubContext(hub);
        long tick = 1;
        var cache = new DashboardContentCache((_, _, _) => Task.FromResult(Result()),
            () => tick, Options.Create(new DashboardMaterializerOptions()));
        var cursor = new RecordingCursor(() => tick);
        // Wire the constrainer's cursor the way BroadcastConstrainerCursorWire does at
        // host start — the BroadcastDirty beacon is skipped for windows opened while the
        // static cursor is unwired, and that static is shared across parallel test
        // classes (order-flaky without this).
        SignalRBroadcastConstrainer.SetCursor(cursor);
        var coord = new DashboardMaterializerCoordinator(
            cache, cursor, new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions()),
            hubContext: hubContext);

        await cache.GetAsync(Traffic, Window(), tick, default);

        var triggered = await coord.MarkDirtyAsync(Traffic.PageKey);

        Assert.True(triggered);
        // The bump + broadcast carry the page's SURFACE KINDS -- the client
        // (sb-live-updates.js) matches a widget's data-sb-depends against the beacon's
        // dirtyKinds, and every depends declares a surface (summary/countries/...), never
        // a page key. The raw "dashboard.traffic" never intersected any widget, which is
        // exactly the unmatchable content-ready ping this change fixes.
        Assert.Contains("summary", cursor.Bumped);
        Assert.DoesNotContain(Traffic.PageKey, cursor.Bumped);

        // The BroadcastDirty beacon carries the full kind set in ONE message (deterministic
        // -- the per-surface BroadcastInvalidation signals land in dictionary order, which
        // the parallel flush emits in a race).
        var beacon = await hub.WaitForDirtyBeaconAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(beacon);
        Assert.Contains("summary", beacon!.DirtyKinds);
        Assert.Contains("countries", beacon.DirtyKinds);
        Assert.DoesNotContain(Traffic.PageKey, beacon.DirtyKinds);
    }

    [Fact]
    public async Task Concurrent_MarkDirtyAsync_and_tick_warm_of_the_same_envelope_compute_only_once()
    {
        // The concurrency-safety guard this relies on: DashboardMaterializerCoordinator's own
        // _inFlightWarms registry (NOT SlidingCacheAtom -- its GetOrComputeAsync does NOT
        // serialize concurrent computes of an identical key on its own; its underlying
        // EphemeralWorkCoordinator is a concurrency-gated queue, not a keyed one, so two
        // WarmAsync calls issued close together can both reach the factory before either
        // caches a result -- confirmed by first running this test calling cache.WarmAsync
        // directly on the second racer, which bypasses the coordinator's guard entirely and
        // reliably computed twice). This test races MarkDirtyAsync against the TICK LOOP'S
        // OWN warm (via MaterializeTickAsync, triggered through the schedule fake) for the
        // SAME live envelope -- both going through the coordinator's WarmEnvelopeAsync choke
        // point -- and asserts exactly one compose happens.
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
        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache, new RecordingCursor(() => tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions { PrewarmDefaultEnvelope = false }),
            sched);

        await cache.GetAsync(Traffic, Window(), tick, default); // make it live, non-blocking factory hasn't fired yet
        await coord.StartAsync(default);

        var tickTask = sched.RaiseTickAsync(TickCadence.Tick10s); // the tick loop's own warm
        await composeStarted.Task; // the tick loop has entered the factory for this envelope

        var markDirtyTask = coord.MarkDirtyAsync(Traffic.PageKey); // races the SAME envelope

        gate.TrySetResult();
        await Task.WhenAll(tickTask, markDirtyTask);

        Assert.Equal(1, composeCount);
    }

    private sealed class RecordingCursor : Mostlylucid.BotDetection.UI.Services.IDashboardChangeCursor
    {
        private readonly Func<long> _tick;
        public RecordingCursor(Func<long> tick) => _tick = tick;
        public List<string> Bumped { get; } = new();
        public long CurrentTick => _tick();
        public void Bump(string surface) => Bumped.Add(surface);
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
