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
///     DIAGNOSTIC (overview- 2026-09-09) for the staging wedge that recurred after the
///     2026-08-28 self-heal:
///     <c>ScheduleCoordinator: subscriber DashboardMaterializerCoordinator on Tick10s has
///     been busy for &gt;2 ticks (skips=9955)</c> — the tick handler permanently busy while
///     the compose itself takes 1.3s, cleared only by a process recreate.
///     <para>
///         WHAT THIS PROVES. The tick pass serialises on <c>_tickGate</c>
///         (<see cref="DashboardMaterializerCoordinator.MaterializeTickAsync"/>, gate acquired
///         before the pass, released in its <c>finally</c>). If one invocation never returns,
///         the gate is held for the process lifetime and EVERY later tick queues behind it —
///         which is exactly the scheduler's "busy for &gt;2 ticks, skips=N" symptom. The
///         self-heal cannot break that: <c>ReArmTickAsync</c> re-subscribes and then calls the
///         SAME <c>MaterializeTickAsync</c>, so the recovery action queues behind the failure
///         it is recovering from. The watchdog can DETECT (it stamps <c>_lastTickUtcTicks</c>
///         and logs warm-inactive) but it cannot RECOVER. Restart is the only exit — which is
///         precisely the observed behaviour.
///     </para>
///     <para>
///         WHY A HUNG COMPOSE IS ENOUGH. The per-warm bound is
///         <c>AwaitWithComposeTimeoutAsync</c>: <c>timeoutMs &lt;= 0</c> awaits the compose
///         UNBOUNDED, and any positive bound only abandons the WAIT — the abandoned compose
///         keeps running. So the wedge precondition is "a compose that never completes" plus a
///         tick that is not bounded away from it.
///     </para>
///     <para>
///         This test asserts the CURRENT (defective) behaviour so the mechanism is pinned
///         rather than described; it is deliberately not a fix. It unblocks the compose at the
///         end so the run cannot leak a stuck task into the suite.
///     </para>
/// </summary>
public sealed class DashboardMaterializerWedgeDiagnosticTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
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
    public async Task A_hung_compose_holds_the_gate_and_the_self_heal_cannot_recover_it()
    {
        var hung = new TaskCompletionSource<DashboardPageResult>();
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) => hung.Task, // never completes — the stuck-compose symptom
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
                ComposeTimeoutMs = 0, // the unbounded branch: AwaitWithComposeTimeoutAsync awaits the compose directly
            }),
            sched);

        await cache.GetAsync(Traffic, Window(), tick, default); // makes the envelope live
        await coord.StartAsync(default);
        tick = 2;

        // 1. The first tick takes the gate and never returns.
        var firstTick = sched.RaiseTickAsync(TickCadence.Tick10s);
        await Task.Delay(250);
        Assert.False(firstTick.IsCompleted,
            "precondition: the hung compose holds _tickGate for the whole pass");

        // 2. Every later tick queues behind the gate → the scheduler sees a permanently busy
        //    subscriber and increments its skip counter forever (the staging symptom).
        var secondTick = sched.RaiseTickAsync(TickCadence.Tick10s);
        await Task.Delay(250);
        Assert.False(secondTick.IsCompleted,
            "a later tick must queue behind the held gate — this is the 'busy for >2 ticks, skips=N' shape");

        // 3. The self-heal is structurally blocked: ReArmTickAsync re-subscribes and then
        //    awaits the SAME MaterializeTickAsync, i.e. the same gate.
        var reArm = coord.ReArmTickAsync(CancellationToken.None);
        await Task.Delay(250);
        Assert.False(reArm.IsCompleted,
            "the watchdog's recovery action deadlocks behind the failure it is recovering from — " +
            "detection without recovery, which is why the wedge survives the self-heal and clears only on a recreate");

        // 4. The only exit today: the compose finally completes (in production, a restart).
        hung.SetResult(Result());
        await Task.WhenAll(firstTick, secondTick, reArm).WaitAsync(TimeSpan.FromSeconds(10));

        await coord.StopAsync(default);
    }
}
