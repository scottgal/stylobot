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
///     The stale-attempt bound (2026-09-09, single-flight follow-up). Plain single-flight caps
///     duplication at one compose per envelope but FREEZES the envelope when an attempt never
///     finishes; superseding a stale attempt keeps the cap (one superseded + one current) while
///     letting the envelope recover.
/// </summary>
public sealed class DashboardMaterializerStaleAttemptTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
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
        DashboardContentCache cache, FakeScheduleCoordinator sched, Func<long> tick)
        => new(cache, new FakeCursor(tick), new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardMaterializerOptions
            {
                PrewarmDefaultEnvelope = false,
                BootPrewarmEnabled = false,
                ComposeTimeoutMs = 100,
                StaleAttemptSeconds = 1,
                WarmInactivityThresholdSeconds = 1,
                FailureRetryBackoffSeconds = 0,
                GlobalMinIntervalSeconds = 0,
            }),
            sched);

    /// <summary>
    ///     A hung attempt must not freeze its envelope forever: once it is stale, exactly one
    ///     replacement starts and the envelope warms again.
    ///     <para>
    ///         THE RETRY PROPERTY, stated because restoring it is the whole point of the bound: a
    ///         FRESH attempt can succeed where the hung one cannot (a leaked connection, a store
    ///         call that never returns), so the envelope must be allowed to try again — which is
    ///         what the 2026-08-21 behaviour had and plain single-flight lost. The bound restores
    ///         it without restoring the unbounded duplication: the replacement is allowed only
    ///         past <c>StaleAttemptSeconds</c> and only while no other superseded attempt is live.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task A_stale_attempt_is_superseded_once_so_the_envelope_recovers()
    {
        var hung = new TaskCompletionSource<DashboardPageResult>();
        var calls = 0;
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) => Interlocked.Increment(ref calls) == 1
                ? hung.Task                      // the stuck attempt
                : Task.FromResult(Result()),     // the replacement is healthy
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        var sched = new FakeScheduleCoordinator();
        var coord = Build(cache, sched, () => tick);
        await cache.GetAsync(Traffic, Window(), 1, default);
        await coord.StartAsync(default);

        try
        {
            tick = 2;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref calls)); // fresh attempt: no replacement yet

            await Task.Delay(1200); // the attempt is now older than StaleAttemptSeconds

            tick = 3;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            tick = 4;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.True(coord.HasWarmedSuccessfully,
                "the superseded attempt must let a fresh compose warm the envelope — that is the point of the bound");
        }
        finally
        {
            hung.TrySetResult(Result());
            await coord.StopAsync(default);
        }
    }

    /// <summary>
    ///     The cap: while a superseded attempt is still running, a later stale attempt must NOT be
    ///     superseded again — otherwise the duplicate-compose count grows without bound, which is
    ///     exactly what single-flight removed. The invariant is "at most TWO concurrent attempts
    ///     per envelope, EVER" — one superseded plus one current — not "a replacement per stale
    ///     interval", which would drift straight back to unbounded.
    /// </summary>
    [Fact]
    public async Task At_most_one_superseded_attempt_is_live_per_envelope()
    {
        var hung = new TaskCompletionSource<DashboardPageResult>();
        var calls = 0;
        long tick = 1;
        var cache = new DashboardContentCache(
            (_, _, _) => { Interlocked.Increment(ref calls); return hung.Task; }, // every attempt hangs
            () => tick,
            Options.Create(new DashboardMaterializerOptions()));

        var sched = new FakeScheduleCoordinator();
        var coord = Build(cache, sched, () => tick);
        await cache.GetAsync(Traffic, Window(), 1, default);
        await coord.StartAsync(default);

        try
        {
            tick = 2;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref calls));

            await Task.Delay(1200);
            tick = 3;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));

            tick = 4; // the replacement starts (the entry was evicted)
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref calls));

            // The replacement is now stale too, but one superseded attempt is still live: the cap
            // must keep this attempt's entry rather than evicting it for a third attempt.
            await Task.Delay(1200);
            tick = 5;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));
            tick = 6;
            await sched.RaiseTickAsync(TickCadence.Tick10s).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally
        {
            hung.TrySetResult(Result());
            await coord.StopAsync(default);
        }
    }
}
