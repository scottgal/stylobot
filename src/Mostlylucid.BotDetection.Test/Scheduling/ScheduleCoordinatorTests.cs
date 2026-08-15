using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Scheduling;

/// <summary>
///     Coverage for <see cref="ScheduleCoordinator"/>. All time is driven by a
///     hand-rolled <see cref="Helpers.FixedTimeProvider"/> and ticks are pumped
///     through the internal <c>TickOnceAsync</c> hook, matching the
///     <c>MeterTriggerService.TickOnceAsync</c> deterministic-loop pattern
///     already used in the codebase. The tests deliberately do NOT take a
///     dependency on <c>Microsoft.Extensions.TimeProvider.Testing</c> --
///     they only need a frozen now, not an advanceable timeline, so a 10-line
///     fake is the right shape.
/// </summary>
public sealed class ScheduleCoordinatorTests
{
    // ---- Subscribe + fire delivers a callback with the tick timestamp -------

    [Fact]
    public async Task Subscribe_callback_fires_with_tick_timestamp()
    {
        var (coord, time) = Build();

        int count = 0;
        DateTimeOffset? observed = null;
        coord.Subscribe(TickCadence.Tick1s, "counter", CostHint.Low, (ts, _) =>
        {
            Interlocked.Increment(ref count);
            observed = ts;
            return Task.CompletedTask;
        });

        var now = time.GetUtcNow();
        await coord.TickOnceAsync(TickCadence.Tick1s);

        count.Should().Be(1);
        observed.Should().Be(now);
    }

    // ---- Multiple subscribers on the same cadence fire in parallel ----------

    [Fact]
    public async Task Multiple_subscribers_on_same_cadence_fire_in_parallel()
    {
        var (coord, _) = Build();

        var gate1 = new TaskCompletionSource();
        var gate2 = new TaskCompletionSource();
        var both = new TaskCompletionSource();
        int inflight = 0;

        Func<DateTimeOffset, CancellationToken, Task> Handler(TaskCompletionSource selfReleased) => async (_, _) =>
        {
            var n = Interlocked.Increment(ref inflight);
            if (n == 2) both.TrySetResult();
            // Wait until the test releases this handler. If parallel fan-out
            // works, both handlers are simultaneously inflight before either
            // resumes; the `both` TCS fires while both are awaiting selfReleased.
            await selfReleased.Task;
            Interlocked.Decrement(ref inflight);
        };

        coord.Subscribe(TickCadence.Tick1s, "a", CostHint.Low, Handler(gate1));
        coord.Subscribe(TickCadence.Tick1s, "b", CostHint.Low, Handler(gate2));

        var tickTask = coord.TickOnceAsync(TickCadence.Tick1s);

        // Both handlers must be inflight together before either is released.
        // 10s, not 2s: under a full parallel test-suite run (thread-pool contention across
        // thousands of concurrent tests) the scheduler dispatching both handlers can take
        // longer than 2s wall-clock even though the fan-out itself is correct -- this is a
        // margin fix against contention, not a correctness change.
        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));

        gate1.SetResult();
        gate2.SetResult();
        await tickTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // ---- Disposing the handle unsubscribes ----------------------------------

    [Fact]
    public async Task Disposing_handle_unsubscribes()
    {
        var (coord, _) = Build();

        int count = 0;
        var handle = coord.Subscribe(TickCadence.Tick1s, "ephemeral", CostHint.Low, (_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        handle.Dispose();

        await coord.TickOnceAsync(TickCadence.Tick1s);
        count.Should().Be(0);
        coord.Snapshot().Should().BeEmpty();
    }

    // ---- A throwing subscriber does NOT prevent siblings --------------------

    [Fact]
    public async Task Throwing_subscriber_does_not_prevent_siblings()
    {
        var (coord, _) = Build();

        int siblingHits = 0;
        coord.Subscribe(TickCadence.Tick1s, "thrower", CostHint.Low, (_, _) =>
            throw new InvalidOperationException("boom"));
        coord.Subscribe(TickCadence.Tick1s, "sibling", CostHint.Low, (_, _) =>
        {
            Interlocked.Increment(ref siblingHits);
            return Task.CompletedTask;
        });

        await coord.TickOnceAsync(TickCadence.Tick1s);

        siblingHits.Should().Be(1);
        var snap = coord.Snapshot();
        snap.Single(s => s.SubscriberName == "thrower").FaultCount.Should().Be(1);
        snap.Single(s => s.SubscriberName == "sibling").FaultCount.Should().Be(0);
    }

    // ---- Re-entry skip: a slow subscriber doesn't re-enter on overlap -------

    [Fact]
    public async Task Slow_subscriber_does_not_re_enter_on_overlap()
    {
        var (coord, _) = Build();

        var release = new TaskCompletionSource();
        int invocations = 0;

        coord.Subscribe(TickCadence.Tick1s, "slow", CostHint.High, async (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            await release.Task;
        });

        // Tick N: starts the long-running invocation.
        var firstTick = coord.TickOnceAsync(TickCadence.Tick1s);
        // Wait until the slow handler is actually running.
        await WaitFor(() => Volatile.Read(ref invocations) == 1);

        // Tick N+1: should observe the busy flag and skip.
        await coord.TickOnceAsync(TickCadence.Tick1s);

        invocations.Should().Be(1, "the second tick must skip while the first is still inflight");
        coord.Snapshot().Single().OverlapSkipCount.Should().Be(1);

        // Cleanup: release the slow handler so the first tick completes.
        release.SetResult();
        await firstTick.WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- OCE on shutdown propagates to subscribers' cancellation token ------

    [Fact]
    public async Task Stop_async_propagates_cancellation_to_inflight_handler()
    {
        var (coord, _) = Build();

        var handlerReady = new TaskCompletionSource();
        var observedCancellation = new TaskCompletionSource<bool>();

        coord.Subscribe(TickCadence.Tick1s, "awaiter", CostHint.Low, async (_, ct) =>
        {
            handlerReady.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
        });

        // Drive the handler via TickOnceAsync (no real cadence loop spin) and
        // call StopAsync to cancel the shutdown CTS. The handler's per-tick CT
        // is linked with the shutdown CTS inside InvokeSubscriberAsync, so the
        // shutdown propagates regardless of how the tick was dispatched.
        var tickTask = coord.TickOnceAsync(TickCadence.Tick1s);
        await handlerReady.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coord.StopAsync(CancellationToken.None);

        (await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(2)))
            .Should().BeTrue();

        // tickTask resolves when the handler finishes (InvokeSubscriberAsync
        // catches the OCE and logs it at Debug). Just make sure it completes.
        await tickTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- Snapshot returns subscriber metadata -------------------------------

    [Fact]
    public void Snapshot_returns_subscriber_metadata()
    {
        var (coord, _) = Build();

        coord.Subscribe(TickCadence.Tick1s, "alpha", CostHint.Low, (_, _) => Task.CompletedTask);
        coord.Subscribe(TickCadence.Tick1m, "beta",  CostHint.High, (_, _) => Task.CompletedTask);

        var snap = coord.Snapshot();
        snap.Should().HaveCount(2);
        snap.Should().Contain(s =>
            s.SubscriberName == "alpha" && s.Cadence == TickCadence.Tick1s && s.Hint == CostHint.Low);
        snap.Should().Contain(s =>
            s.SubscriberName == "beta"  && s.Cadence == TickCadence.Tick1m && s.Hint == CostHint.High);

        // Never invoked => null timestamps.
        foreach (var s in snap)
        {
            s.LastInvokedAt.Should().BeNull();
            s.LastDuration.Should().BeNull();
        }
    }

    // ---- OverlapWarnEveryNth controls the log threshold ---------------------

    [Fact]
    public async Task OverlapWarnEveryNth_controls_log_threshold()
    {
        var options = new ScheduleCoordinatorOptions { OverlapWarnEveryNth = 2 };
        var logger = new RecordingLogger();
        var (coord, _) = Build(options, logger);

        var release = new TaskCompletionSource();
        int invocations = 0;

        coord.Subscribe(TickCadence.Tick1s, "stuck", CostHint.High, async (_, _) =>
        {
            Interlocked.Increment(ref invocations);
            await release.Task;
        });

        // Tick 1: starts the long-running invocation.
        var firstTick = coord.TickOnceAsync(TickCadence.Tick1s);
        await WaitFor(() => Volatile.Read(ref invocations) == 1);

        // Pump 5 overlapping ticks -- each is skipped.
        for (int i = 0; i < 5; i++)
            await coord.TickOnceAsync(TickCadence.Tick1s);

        invocations.Should().Be(1);
        var snap = coord.Snapshot().Single();
        snap.OverlapSkipCount.Should().Be(5);

        // With OverlapWarnEveryNth=2, skips at counts 2 and 4 fire warnings.
        // Skips at 1, 3, 5 do not. => exactly 2 warning lines.
        logger.WarningCount.Should().Be(2);

        release.SetResult();
        await firstTick.WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- Disabled-cadence option ignores ticks ------------------------------

    [Fact]
    public async Task Disabled_cadence_does_not_fire()
    {
        var options = new ScheduleCoordinatorOptions { EnableTick1s = false };
        var (coord, _) = Build(options);

        int count = 0;
        coord.Subscribe(TickCadence.Tick1s, "disabled-bound", CostHint.Low, (_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        await coord.TickOnceAsync(TickCadence.Tick1s);
        count.Should().Be(0);
    }

    // ---- Wall-clock alignment helper ----------------------------------------

    [Fact]
    public void NextBoundary_aligns_to_period_from_midnight()
    {
        // 12:34:56.789 UTC, tick.1m cadence => 12:35:00 UTC
        var now = new DateTime(2026, 6, 10, 12, 34, 56, 789, DateTimeKind.Utc);
        ScheduleCoordinator.NextBoundary(now, TimeSpan.FromMinutes(1))
            .Should().Be(new DateTime(2026, 6, 10, 12, 35, 0, DateTimeKind.Utc));

        // 12:34:56 UTC, tick.5m cadence => 12:35:00 UTC
        ScheduleCoordinator.NextBoundary(now, TimeSpan.FromMinutes(5))
            .Should().Be(new DateTime(2026, 6, 10, 12, 35, 0, DateTimeKind.Utc));

        // 12:37:00 UTC, tick.5m cadence => 12:40:00 UTC
        var t2 = new DateTime(2026, 6, 10, 12, 37, 0, DateTimeKind.Utc);
        ScheduleCoordinator.NextBoundary(t2, TimeSpan.FromMinutes(5))
            .Should().Be(new DateTime(2026, 6, 10, 12, 40, 0, DateTimeKind.Utc));

        // 12:34:56.789 UTC, tick.1h cadence => 13:00:00 UTC
        ScheduleCoordinator.NextBoundary(now, TimeSpan.FromHours(1))
            .Should().Be(new DateTime(2026, 6, 10, 13, 0, 0, DateTimeKind.Utc));
    }

    // ---- Detached cadence: hung subscriber must NOT stall siblings ----------

    [Fact]
    public async Task TickOnceDetached_returns_synchronously_even_when_a_subscriber_hangs()
    {
        // Regression: production RunCadenceLoop fires ticks fire-and-forget so
        // a hung subscriber on tick N cannot block subscribers on tick N+1.
        // Pre-fix the loop awaited FireTickAsync -> Task.WhenAll, so a hung
        // handler also hung the watchdog's own bookkeeping subscriber and the
        // gateway got SIGTERM'd every ~60s on staging. This test calls the
        // production-shape entry (TickOnceDetached) and asserts the call
        // returns even though one subscriber is parked on a TaskCompletionSource.
        var (coord, _) = Build();

        var parked = new TaskCompletionSource();
        coord.Subscribe(TickCadence.Tick10s, "hangs-forever", CostHint.Low, async (_, _) =>
        {
            await parked.Task; // never released for the duration of the test
        });

        int siblingHits = 0;
        coord.Subscribe(TickCadence.Tick10s, "sibling-counter", CostHint.Low, (_, _) =>
        {
            Interlocked.Increment(ref siblingHits);
            return Task.CompletedTask;
        });

        // Fire 5 ticks via the production-shape detached path. The pre-fix
        // production code would have awaited FireTickAsync -> Task.WhenAll
        // and hung on tick 1; the post-fix production code (and this test
        // hook) discards the returned Task and returns immediately.
        coord.TickOnceDetached(TickCadence.Tick10s);
        coord.TickOnceDetached(TickCadence.Tick10s);
        coord.TickOnceDetached(TickCadence.Tick10s);
        coord.TickOnceDetached(TickCadence.Tick10s);
        coord.TickOnceDetached(TickCadence.Tick10s);

        // The sibling counter handler completes synchronously, so by the time
        // each TickOnceDetached call returns, the sibling for THAT tick has
        // already executed. Five ticks => five hits. WaitFor handles the
        // hand-off scheduling delay.
        await WaitFor(() => Volatile.Read(ref siblingHits) >= 5, timeoutMs: 2000);
        siblingHits.Should().Be(5,
            "the production cadence loop must not await FireTickAsync, so a hung " +
            "subscriber on one tick cannot starve a fast sibling on the next tick");

        // Release the hung handler so the test exits cleanly.
        parked.SetResult();
    }

    [Fact]
    public async Task Hung_subscriber_skips_itself_via_BusyFlag_but_does_not_skip_siblings()
    {
        // Pairs with TickOnceDetached_returns_synchronously: prove the
        // single-flight guard is per-subscriber, not per-cadence. The slow
        // subscriber's BusyFlag CAS rejects its OWN re-entry on subsequent
        // ticks while the fast sibling continues to be invoked.
        var (coord, _) = Build();

        int slowEntries = 0;
        var parked = new TaskCompletionSource();
        coord.Subscribe(TickCadence.Tick10s, "slow", CostHint.Low, async (_, _) =>
        {
            Interlocked.Increment(ref slowEntries);
            await parked.Task;
        });

        int fastHits = 0;
        coord.Subscribe(TickCadence.Tick10s, "fast", CostHint.Low, (_, _) =>
        {
            Interlocked.Increment(ref fastHits);
            return Task.CompletedTask;
        });

        for (var i = 0; i < 4; i++) coord.TickOnceDetached(TickCadence.Tick10s);

        await WaitFor(() => Volatile.Read(ref fastHits) >= 4, timeoutMs: 2000);

        // slow ran once and is parked; the BusyFlag rejects the remaining
        // three invocations. fast ran four times because it returns
        // synchronously and never trips its own BusyFlag.
        slowEntries.Should().Be(1, "BusyFlag CAS must reject re-entry while the previous invocation is still running");
        fastHits.Should().Be(4, "the fast subscriber must not be starved by the slow one");

        parked.SetResult();
    }

    [Fact]
    public async Task Cadence_loop_fault_does_not_kill_ticks_forever()
    {
        // Tick-death regression (operator directive 2026-08-15): the cadence
        // loop's generic catch previously exited FOREVER on any non-shutdown
        // fault ("ticks lost until restart") -- a silent tick-death that froze
        // every subscriber on the cadence until a redeploy (the staging
        // 2026-08-15 01:29 freeze class). The loop must self-heal: fault ->
        // loud log -> bounded backoff -> re-enter at the next wall-clock
        // boundary. Only shutdown may exit.
        var firstTick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FaultOnceTimeProvider(TimeProvider.System);
        var options = new ScheduleCoordinatorOptions
        {
            LoopFaultBackoff = TimeSpan.FromMilliseconds(100),
            LoopFaultMaxBackoff = TimeSpan.FromMilliseconds(100),
            OverlapWarnEveryNth = int.MaxValue
        };
        var coord = new ScheduleCoordinator(
            Options.Create(options),
            NullLogger<ScheduleCoordinator>.Instance,
            provider);

        coord.Subscribe(TickCadence.Tick1s, "AliveSubscriber", CostHint.Low,
            (_, _) => { firstTick.TrySetResult(); return Task.CompletedTask; });

        var loop = coord.RunCadenceLoop(TickCadence.Tick1s, TimeSpan.FromSeconds(1));

        // The injected fault fires on the loop's FIRST GetUtcNow (alignment).
        // The loop must back off and re-enter; the subscriber must still
        // receive a tick afterwards.
        var completed = await Task.WhenAny(firstTick.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        completed.Should().Be(firstTick.Task,
            "the tick source must survive a cadence-loop fault — ticks are NOT lost until restart");

        provider.FaultCount.Should().Be(1, "exactly one fault was injected; the loop re-entered past it");

        await coord.StopAsync(CancellationToken.None);
        await loop;
    }

    // ---- Helpers ------------------------------------------------------------

    private static (ScheduleCoordinator coord, FixedTimeProvider time) Build(
        ScheduleCoordinatorOptions? options = null,
        ILogger<ScheduleCoordinator>? logger = null)
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        var coord = new ScheduleCoordinator(
            Options.Create(options ?? new ScheduleCoordinatorOptions()),
            logger ?? NullLogger<ScheduleCoordinator>.Instance,
            time);
        return (coord, time);
    }

    private static async Task WaitFor(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount + timeoutMs;
        while (Environment.TickCount < deadline)
        {
            if (predicate()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("WaitFor predicate did not become true within timeout.");
    }

    /// <summary>
    ///     Tiny recording logger used by <see cref="OverlapWarnEveryNth_controls_log_threshold"/>
    ///     to assert the per-Nth log threshold.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ScheduleCoordinator>
    {
        private int _warningCount;
        public int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Interlocked.Increment(ref _warningCount);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    ///     <see cref="TimeProvider"/> that throws from <see cref="GetUtcNow"/>
    ///     exactly once, then delegates to the inner provider. Used to fault
    ///     the cadence loop at its first alignment read and prove the loop
    ///     re-enters instead of exiting forever.
    /// </summary>
    private sealed class FaultOnceTimeProvider : TimeProvider
    {
        private readonly TimeProvider _inner;
        private int _faulted;

        public FaultOnceTimeProvider(TimeProvider inner) => _inner = inner;

        /// <summary>1 once the single injected fault has fired.</summary>
        public int FaultCount => Volatile.Read(ref _faulted);

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.CompareExchange(ref _faulted, 1, 0) == 0)
                throw new InvalidOperationException("injected cadence-loop fault");
            return _inner.GetUtcNow();
        }
    }
}
