using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     The materializer WEDGE, asserted from the operator's bar (2026-09-09 ruling): the
///     coordinator must RECOVER from a stuck pass without a process restart, must never serialise
///     the whole tick behind an unbounded await, and must never run two composes for one envelope.
///     <para>
///         The measured mechanism is the UNBOUNDED BRANCH, not a synchronous block in the compose:
///         <c>ComposeTimeoutMs &lt;= 0</c> used to await the compose directly, so one compose that
///         never returns held <c>_tickGate</c> for the process lifetime, every later tick queued
///         behind it (<c>ScheduleCoordinator: ... busy for &gt;2 ticks (skips=N)</c>), and
///         <c>ReArmTickAsync</c> — the self-heal — awaited the same gate. (A blocking compose
///         delegate does not block the tick thread: it runs on the cache atom's own work
///         coordinator.) Restart was the only exit.
///     </para>
///     <para>
///         Every test here fails on the pre-fix code; the superseded behaviour stays recorded in
///         <see cref="DashboardMaterializerWedgeDiagnosticTests"/>.
///     </para>
/// </summary>
public sealed class DashboardMaterializerStuckPassRecoveryTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
    private static readonly DashboardPageManifest Threats = new("dashboard.threats", new[] { "threats" });

    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);
    private static DashboardPageResult Result() => new(new DashboardDatasetBundle(null, null, null, null, null));

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

    private sealed class FakeCursor : IDashboardChangeCursor
    {
        private readonly Func<long> _tick;
        public FakeCursor(Func<long> tick) => _tick = tick;
        public long CurrentTick => _tick();
        public void Bump(string surface) { }
        public long TickFor(string surface) => 0;
        public IReadOnlyList<string> SurfacesChangedThisTick() => Array.Empty<string>();
    }

    private static DashboardMaterializerCoordinator Build(
        DashboardContentCache cache, FakeScheduleCoordinator sched, Func<long> tick, DashboardMaterializerOptions options)
        => new(cache, new FakeCursor(tick), new DefaultDashboardPageManifestSource(), Options.Create(options), sched);

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>
    ///     The gate must not be held unboundedly: while one pass is stuck inside a long compose,
    ///     a LATER pass must still get through and do its work. On the pre-fix code the later pass
    ///     queues on <c>_tickGate</c> forever — which is the <c>busy for &gt;2 ticks, skips=N</c>
    ///     symptom, and why <c>ReArmTickAsync</c> (the self-heal) could not recover either.
    ///     <para>
    ///         The observable is the change cursor: <c>MaterializeTickAsync</c> reads
    ///         <see cref="IDashboardChangeCursor.CurrentTick"/> immediately AFTER taking the gate,
    ///         so a second read proves the later pass crossed the gate while the first is still
    ///         stuck. (The compose itself is deliberately left stuck, so nothing else about the
    ///         pass is observable — and the cache atom's own work coordinator would serialise a
    ///         second compose behind the stuck one anyway.)
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_stuck_pass_does_not_serialise_a_later_pass_behind_the_gate()
    {
        var release = new ManualResetEventSlim(false);
        var cursorReads = 0;
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) =>
            {
                release.Wait(); // the compose blocks synchronously, indefinitely
                return Task.FromResult(Result());
            },
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        var sched = new FakeScheduleCoordinator();
        var coord = Build(cache, sched,
            () => { Interlocked.Increment(ref cursorReads); return tick; },
            new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                BootPrewarmEnabled = false,
                // Long bound: this pass is meant to stay stuck for the duration of the test, so
                // only the GATE bound can let the later pass through.
                ComposeTimeoutMs = 60_000,
                WarmInactivityThresholdSeconds = 1,
                FailureRetryBackoffSeconds = 0,
            });

        await cache.GetAsync(Traffic, Window(), 1, default);
        await coord.StartAsync(default);

        try
        {
            // Pass 1: takes the gate, reads the cursor, and wedges inside the traffic compose.
            tick = 2;
            var firstPass = sched.RaiseTickAsync(TickCadence.Tick10s);
            await Task.Delay(200);
            Assert.False(firstPass.IsCompleted, "precondition: the compose holds the pass (and the gate)");
            Assert.Equal(1, Volatile.Read(ref cursorReads));

            // Pass 2: must get PAST the stuck holder of _tickGate and start its own work.
            tick = 3;
            _ = sched.RaiseTickAsync(TickCadence.Tick10s);

            Assert.True(await WaitUntilAsync(() => Volatile.Read(ref cursorReads) >= 2, TimeSpan.FromSeconds(8)),
                "a later pass must get past a stuck holder of _tickGate — otherwise the whole tick is " +
                "serialised behind an unbounded await and only a process restart clears it");
        }
        finally
        {
            release.Set();
            await coord.StopAsync(default);
        }
    }

    /// <summary>
    ///     THE SINGLE-FLIGHT INVARIANT: at most ONE compose per envelope is ever in flight, even
    ///     when an attempt is abandoned at the compose bound and later passes run ungated.
    ///     <para>
    ///         Why it must hold in code, not just in a comment: <c>SlidingCacheAtom.GetOrComputeAsync</c>
    ///         has no per-key in-flight map — a miss enqueues a request into
    ///         <c>EphemeralWorkCoordinator</c>, which is a concurrency-gated queue, NOT a keyed one
    ///         (<c>EphemeralWorkCoordinator.EnqueueAsync</c> writes to a channel). So two concurrent
    ///         misses for the same key BOTH reach the compose. The coordinator's <c>_inFlightWarms</c>
    ///         Lazy is the only thing preventing that — and it only works while the entry exists.
    ///         The pre-fix code evicted the entry when an awaiter gave up (the abandonment path), so
    ///         a later pass started a SECOND compose while the abandoned one still ran.
    ///     </para>
    ///     <para>
    ///         Asserted across three ticks: the stuck envelope's compose is entered exactly once,
    ///         and the healthy envelope still warms — the coordinator recovers without stacking
    ///         duplicate work. The stuck shape is a compose that returns a task that never
    ///         completes (the realistic store hang); the cache atom's work coordinator frees the
    ///         body's slot on its own body timeout, so the duplicate really does reach the compose.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_hung_compose_is_never_started_twice_for_the_same_envelope()
    {
        var hung = new TaskCompletionSource<DashboardPageResult>();
        var hungCalls = 0;
        var healthyCalls = 0;
        long tick = 1;
        var cache = new DashboardContentCache(
            (manifest, _, _) =>
            {
                if (manifest.PageKey == Traffic.PageKey)
                {
                    Interlocked.Increment(ref hungCalls);
                    return hung.Task; // never completes
                }
                Interlocked.Increment(ref healthyCalls);
                return Task.FromResult(Result());
            },
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        var sched = new FakeScheduleCoordinator();
        var coord = Build(cache, sched, () => tick, new DashboardMaterializerOptions
        {
            PrewarmDefaultEnvelope = false,
            BootPrewarmEnabled = false,
            ComposeTimeoutMs = 200,
            WarmInactivityThresholdSeconds = 1,
            FailureRetryBackoffSeconds = 0,
        });

        await cache.GetAsync(Traffic, Window(), 1, default);
        await cache.GetAsync(Threats, Window(), 1, default);
        await coord.StartAsync(default);

        try
        {
            for (var i = 2; i <= 4; i++)
            {
                tick = i;
                await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(1, Volatile.Read(ref hungCalls));
            Assert.True(Volatile.Read(ref healthyCalls) >= 1,
                "the healthy envelope must still warm — the coordinator recovers without duplicating work");
            Assert.True(coord.HasWarmedSuccessfully);
        }
        finally
        {
            hung.TrySetResult(Result());
            await coord.StopAsync(default);
        }
    }
}
