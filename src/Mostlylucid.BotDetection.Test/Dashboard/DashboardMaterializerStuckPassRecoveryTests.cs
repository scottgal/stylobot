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
///     coordinator must RECOVER from a stuck pass without a process restart, and must never
///     serialise the whole tick behind an unbounded await.
///     <para>
///         The staging mechanism is the SECOND wedge candidate: the compose blocks the calling
///         thread SYNCHRONOUSLY before returning a task. <c>lazy.Value</c> evaluates the factory
///         on the tick thread, so <see cref="DashboardMaterializerOptions.ComposeTimeoutMs"/> can
///         never fire — the bound is only applied AFTER the factory returns. The pass therefore
///         never returns, holds <c>_tickGate</c> for the process lifetime, and every later tick
///         (and <c>ReArmTickAsync</c>, the self-heal) queues behind it:
///         <c>ScheduleCoordinator: subscriber DashboardMaterializerCoordinator on Tick10s has
///         been busy for &gt;2 ticks (skips=N)</c>. Restart was the only exit.
///     </para>
///     <para>
///         Both tests fail on the pre-fix code and are the proof the fix works; the superseded
///         behaviour stays recorded in
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
}
