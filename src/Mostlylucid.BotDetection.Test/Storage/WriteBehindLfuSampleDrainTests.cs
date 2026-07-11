using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Storage;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Storage;

/// <summary>
///     Gap A: the behavioural-sample drain. Each cycle the drainer pulls the dirty
///     keys' CURRENT values, coalesced by key, ordered by significance
///     (<c>ColdnessScore</c> descending), and persists each once. A hot shape that
///     mutated many times this cycle persists ONCE; an unchanged shape does not
///     persist; significance decides which dirty shapes flush first under a batch cap.
/// </summary>
public sealed class WriteBehindLfuSampleDrainTests
{
    private sealed record Val(string Id, int Version, long Significance);

    /// <summary>Test double: opts into the sample drain, records what it persists.</summary>
    private sealed class Accumulator : WriteBehindLfuStore<string, Val, Val>
    {
        public readonly ConcurrentQueue<IReadOnlyList<KeyValuePair<string, Val>>> Flushes = new();

        public Accumulator(int batchMaxSize = 50)
            : base(maxEntries: 10_000, writeQueueCapacity: 10_000, batchMaxSize,
                   drainInterval: TimeSpan.FromMilliseconds(25), NullLogger.Instance) { }

        protected override bool UseBehaviouralSampleDrain => true;
        protected override Val CreateInitial(string key, Val op) => op;
        protected override Val MergeIntoExisting(string key, Val existing, Val op) => op; // latest wins
        protected override ValueTask<Val?> LoadFromDurableTierAsync(string key, CancellationToken ct)
            => new((Val?)null);
        // Abstract op-path persist: never called on the sample path, must exist.
        protected override Task PersistBatchAsync(IReadOnlyList<Val> batch, CancellationToken ct)
            => Task.CompletedTask;
        protected override long ColdnessScore(Val entry) => entry.Significance;

        protected override Task PersistValuesBatchAsync(
            IReadOnlyList<KeyValuePair<string, Val>> batch, CancellationToken ct)
        {
            Flushes.Enqueue(batch.ToList());
            return Task.CompletedTask;
        }

        public List<KeyValuePair<string, Val>> AllPersisted() =>
            Flushes.SelectMany(f => f).ToList();
    }

    /// <summary>
    ///     Test double whose durable-tier persist always throws. Drain interval is set far in
    ///     the future so the background drainer never fires during the test: FlushDirtyAsync is
    ///     the sole drainer, so the persist failure it hits cannot be masked by a concurrent
    ///     background pass claiming the key first.
    /// </summary>
    private sealed class FailingStore : WriteBehindLfuStore<string, Val, Val>
    {
        public FailingStore()
            : base(maxEntries: 10_000, writeQueueCapacity: 10_000, batchMaxSize: 50,
                   drainInterval: TimeSpan.FromHours(1), NullLogger.Instance) { }

        protected override bool UseBehaviouralSampleDrain => true;
        protected override Val CreateInitial(string key, Val op) => op;
        protected override Val MergeIntoExisting(string key, Val existing, Val op) => op;
        protected override ValueTask<Val?> LoadFromDurableTierAsync(string key, CancellationToken ct)
            => new((Val?)null);
        protected override Task PersistBatchAsync(IReadOnlyList<Val> batch, CancellationToken ct)
            => Task.CompletedTask;
        protected override long ColdnessScore(Val entry) => entry.Significance;

        protected override Task PersistValuesBatchAsync(
            IReadOnlyList<KeyValuePair<string, Val>> batch, CancellationToken ct)
            => throw new InvalidOperationException("durable tier is down");
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!cond() && Environment.TickCount64 < deadline)
            await Task.Delay(15);
    }

    [Fact]
    public async Task HotKey_MutatedManyTimes_PersistsOnce_WithCurrentValue()
    {
        using var store = new Accumulator();

        // 500 mutations to one key land (synchronously) inside one drain interval.
        for (var v = 1; v <= 500; v++) store.Record("A", new Val("A", v, Significance: 10));
        store.Record("B", new Val("B", 1, Significance: 5));

        await WaitUntilAsync(() => store.AllPersisted().Count >= 2);
        await Task.Delay(80); // give any extra cycles a chance to (wrongly) re-persist

        var persisted = store.AllPersisted();
        persisted.Count(p => p.Key == "A").Should().Be(1,
            "a hot key coalesces to a single durable write per cycle, not one per mutation");
        persisted.Single(p => p.Key == "A").Value.Version.Should().Be(500,
            "the persisted value is the current shape, not a stale op");
        persisted.Count(p => p.Key == "B").Should().Be(1);
    }

    [Fact]
    public async Task UnchangedKey_DoesNotRePersist()
    {
        using var store = new Accumulator();
        store.Record("A", new Val("A", 1, Significance: 10));

        await WaitUntilAsync(() => store.AllPersisted().Count >= 1);
        var afterFirst = store.AllPersisted().Count;

        // No further mutations: several drain cycles must add nothing.
        await Task.Delay(120);
        store.AllPersisted().Count.Should().Be(afterFirst,
            "a settled, unchanged shape is not re-persisted every cycle");
    }

    [Fact]
    public async Task OverBatchCap_HighestSignificanceFlushesFirst_RestFlushesOnceBatchHasRoom()
    {
        using var store = new Accumulator(batchMaxSize: 2);

        // Three dirty keys, distinct significance, all within one cycle.
        store.Record("low", new Val("low", 1, Significance: 10));
        store.Record("mid", new Val("mid", 1, Significance: 20));
        store.Record("high", new Val("high", 1, Significance: 30));

        // First flush must be the two highest-significance keys.
        await WaitUntilAsync(() => store.Flushes.TryPeek(out var f) && f.Count == 2);
        var first = store.Flushes.First();
        first.Select(kv => kv.Key).Should().BeEquivalentTo(new[] { "high", "mid" },
            "the batch cap admits the highest-significance dirty shapes first");

        // Once the high-significance keys are drained there is room, so the deferred
        // low-significance key flushes on a subsequent cycle. NOTE: this is NOT a
        // no-starvation guarantee: under SUSTAINED high-significance saturation the
        // batch stays full every cycle and a low-significance key can be deferred
        // indefinitely, which is correct sampling behaviour (insignificant shapes are
        // exactly the ones it is OK to not persist; _dirtyKeys stays bounded as a
        // subset of _entries). Here saturation stops, so the tail drains.
        await WaitUntilAsync(() => store.AllPersisted().Any(p => p.Key == "low"));
        store.AllPersisted().Select(p => p.Key).Should().Contain("low");
    }

    [Fact]
    public async Task FlushDirtyAsync_SurfacesPersistError_InsteadOfSilentSuccess()
    {
        using var store = new FailingStore();
        store.Record("A", new Val("A", 1, Significance: 10));

        // As a durability barrier, a dead durable tier must THROW, not return as if it flushed.
        // Before the fix, DrainDirtyOnceAsync swallowed the error and returned 0, so FlushDirtyAsync
        // broke out of its loop and completed normally with the key still unpersisted.
        var flush = async () => await store.FlushDirtyAsync();
        await flush.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durable tier is down*");
    }

    /// <summary>Test double that parks its FIRST persist (the background drainer's) mid-flight
    ///     so a concurrent FlushDirtyAsync is forced to race it; later persists complete
    ///     immediately.</summary>
    private sealed class ParkingStore : WriteBehindLfuStore<string, Val, Val>
    {
        private int _firstPersist = 1;
        public readonly TaskCompletionSource FirstPersistStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseFirstPersist = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<IReadOnlyList<KeyValuePair<string, Val>>> Flushes = new();

        public ParkingStore()
            : base(maxEntries: 10_000, writeQueueCapacity: 10_000, batchMaxSize: 50,
                   drainInterval: TimeSpan.FromMilliseconds(20), NullLogger.Instance) { }

        protected override bool UseBehaviouralSampleDrain => true;
        protected override Val CreateInitial(string key, Val op) => op;
        protected override Val MergeIntoExisting(string key, Val existing, Val op) => op;
        protected override ValueTask<Val?> LoadFromDurableTierAsync(string key, CancellationToken ct)
            => new((Val?)null);
        protected override Task PersistBatchAsync(IReadOnlyList<Val> batch, CancellationToken ct)
            => Task.CompletedTask;
        protected override long ColdnessScore(Val entry) => entry.Significance;

        protected override async Task PersistValuesBatchAsync(
            IReadOnlyList<KeyValuePair<string, Val>> batch, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _firstPersist, 0) == 1)
            {
                FirstPersistStarted.TrySetResult();
                await ReleaseFirstPersist.Task; // park mid-persist, holding the drain gate
            }
            Flushes.Enqueue(batch.ToList());
        }

        public List<KeyValuePair<string, Val>> AllPersisted() => Flushes.SelectMany(f => f).ToList();
    }

    [Fact]
    public async Task FlushDirtyAsync_WaitsFor_InFlightDrainerBatch_BeforeReturning()
    {
        using var store = new ParkingStore();

        // The background drainer claims "A" and parks mid-persist, holding the drain gate.
        // (Pre-fix, "A" is already TryRemoved from _dirtyKeys during that persist.)
        store.Record("A", new Val("A", 1, Significance: 10));
        await store.FirstPersistStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        // Mark more keys dirty while the drainer is parked.
        store.Record("B", new Val("B", 1, Significance: 20));
        store.Record("C", new Val("C", 1, Significance: 30));

        // FlushDirtyAsync must NOT return while the drainer holds an in-flight persist.
        // Pre-fix it saw _dirtyKeys non-empty (B/C), drained those, then found it empty
        // (A was claimed) and returned -- with A never persisted.
        var flushTask = store.FlushDirtyAsync();
        await Task.Delay(80);
        flushTask.IsCompleted.Should().BeFalse(
            "FlushDirtyAsync returned while the background drainer still had a persist in flight");

        // Release the parked persist; flush can now acquire the gate + drain the remainder.
        store.ReleaseFirstPersist.TrySetResult();
        await flushTask.WaitAsync(TimeSpan.FromSeconds(3));

        // On return, every key dirty at call time is durable -- including the one the
        // drainer had in flight.
        store.AllPersisted().Select(p => p.Key).Should().Contain(new[] { "A", "B", "C" });
    }
}
