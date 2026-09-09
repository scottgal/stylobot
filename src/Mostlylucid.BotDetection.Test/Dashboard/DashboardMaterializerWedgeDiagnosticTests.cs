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
///     The materializer WEDGE (overview- 2026-09-09 diagnostic, re-pinned to the fixed
///     behaviour): the staging symptom was
///     <c>ScheduleCoordinator: subscriber DashboardMaterializerCoordinator on Tick10s has been
///     busy for &gt;2 ticks (skips=9955)</c> — the tick handler permanently busy while the compose
///     itself takes 1.3s, cleared only by a process recreate.
///     <para>
///         WHAT IT PROVES NOW. The pass serialises on <c>_tickGate</c>
///         (<see cref="DashboardMaterializerCoordinator.MaterializeTickAsync"/>, gate acquired
///         before the pass, released in its <c>finally</c>). The wedge precondition was "a compose
///         that never completes" PLUS a pass that is not bounded away from it:
///         <c>AwaitWithComposeTimeoutAsync</c> used to await the compose UNBOUNDED when
///         <c>ComposeTimeoutMs &lt;= 0</c>, so one such compose held the gate for the process
///         lifetime, every later tick queued behind it, and <c>ReArmTickAsync</c> — the self-heal —
///         awaited the SAME gate, i.e. deadlocked behind the failure it was recovering from.
///         Detection without recovery; restart was the only exit.
///     </para>
///     <para>
///         THE FIX (2026-09-09 ruling): a non-positive <c>ComposeTimeoutMs</c> is clamped to the
///         documented default (no unbounded await, ever), the compose is evaluated off the caller's
///         thread so the bound actually applies to a factory that blocks before its first await,
///         and the gate wait itself is bounded — a pass that has held it past
///         <see cref="DashboardMaterializerOptions.WarmInactivityThresholdSeconds"/> is declared
///         stuck and later passes proceed without it.
///     </para>
///     <para>
///         AND THE INVARIANT THAT MAKES THE UNGATED PATH SAFE: at most ONE compose per envelope
///         is in flight, ever. The pre-fix cleanup removed the <c>_inFlightWarms</c> entry as soon
///         as an awaiter gave up, so the next pass published a fresh Lazy and a SECOND compose for
///         the same envelope ran alongside the abandoned one — and the cache atom keeps no per-key
///         in-flight map, so both reached the compose. The entry now clears only when the attempt
///         FINISHES. This test asserts exactly that: the hung attempt is entered once.
///     </para>
///     <para>
///         The superseded behaviour (the pass awaited forever) is what this test asserted before
///         the fix; the test was INVERTED, not deleted, so the mechanism stays pinned rather than
///         described. The fast gate-serialisation proof lives in
///         <see cref="DashboardMaterializerStuckPassRecoveryTests"/>.
///     </para>
/// </summary>
public sealed class DashboardMaterializerWedgeDiagnosticTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
    private static readonly DashboardPageManifest Threats = new("dashboard.threats", new[] { "threats" });
    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);
    private static DashboardPageResult Result() => new(new DashboardDatasetBundle(null, null, null, null, null));

    /// <summary>Mirrors the suite's fake: a schedule the test drives tick-by-tick.</summary>
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

    [Fact]
    public async Task A_hung_compose_no_longer_wedges_the_pass_and_is_never_started_twice()
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
                    return hung.Task;                 // the stuck compose — never completes
                }
                Interlocked.Increment(ref healthyCalls);
                return Task.FromResult(Result());     // a healthy envelope
            },
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        var sched = new FakeScheduleCoordinator();
        var coord = new DashboardMaterializerCoordinator(
            cache,
            new FakeCursor(() => tick),
            new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                BootPrewarmEnabled = false,
                ComposeTimeoutMs = 0, // the unbounded branch — now clamped to the documented default
                WarmInactivityThresholdSeconds = 1,
                FailureRetryBackoffSeconds = 0,
            }),
            sched);

        await cache.GetAsync(Traffic, Window(), tick, default);
        await cache.GetAsync(Threats, Window(), tick, default);
        await coord.StartAsync(default);

        try
        {
            // THE PASS RETURNS (the clamp): pre-fix this awaited the hung compose forever and
            // held _tickGate for the process lifetime.
            tick = 2;
            var allowed = TimeSpan.FromMilliseconds(DashboardMaterializerOptions.DefaultComposeTimeoutMs + 10_000);
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(allowed);

            // THE INVARIANT: the hung attempt is entered exactly ONCE, and the healthy envelope
            // still warms — the coordinator recovers without stacking duplicate composes.
            Assert.Equal(1, Volatile.Read(ref hungCalls));
            Assert.True(Volatile.Read(ref healthyCalls) >= 1,
                "a healthy envelope must still warm while another envelope's compose hangs");
            Assert.True(coord.HasWarmedSuccessfully,
                "the coordinator must keep making progress — recovery without a process restart");
        }
        finally
        {
            hung.TrySetResult(Result()); // observe the abandoned compose so the run cannot leak it
            await coord.StopAsync(default);
        }
    }
}
