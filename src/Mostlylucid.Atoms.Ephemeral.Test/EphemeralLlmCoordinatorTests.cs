using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.Atoms.Ephemeral;
using Mostlylucid.Common.Scheduling;
using Xunit;

namespace Mostlylucid.Atoms.Ephemeral.Test;

public class EphemeralLlmCoordinatorTests
{
    private sealed record Item(int Id);
    private sealed record Result(int ItemId, string Label);

    private sealed class FakeSchedule : IScheduleCoordinator
    {
        public Func<DateTimeOffset, CancellationToken, Task>? Handler;
        public IDisposable Subscribe(TickCadence c, string name, CostHint h, Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            Handler = handler;
            return new Sub(() => Handler = null);
        }
        public IReadOnlyList<TickSubscriberMetadata> Snapshot() => Array.Empty<TickSubscriberMetadata>();
        public Task FireAsync(CancellationToken ct = default) => Handler!.Invoke(DateTimeOffset.UtcNow, ct);
        private sealed class Sub(Action onDispose) : IDisposable
        {
            public void Dispose() => onDispose();
        }
    }

    private sealed class StaticPicker(params Item[] items) : IEphemeralPicker<Item>
    {
        public IReadOnlyList<Item> Pick(int maxCount) => items.Take(maxCount).ToArray();
    }

    private sealed class PassthroughPrompter : IEphemeralPrompter<Item>
    {
        public EphemeralPrompt Build(Item item) => new("sys", $"user-{item.Id}", 100, 0.0);
    }

    private sealed class CountingInvoker(Func<EphemeralPrompt, CancellationToken, Task<Result>> impl) : IEphemeralLlmInvoker<Result>
    {
        public int Calls;
        public Task<Result> InvokeAsync(EphemeralPrompt p, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return impl(p, ct);
        }
    }

    private sealed class CollectingWriteback : IEphemeralWriteback<Item, Result>
    {
        public ConcurrentBag<(Item, Result)> Written = new();
        public Task ApplyAsync(Item item, Result result, CancellationToken ct)
        {
            Written.Add((item, result));
            return Task.CompletedTask;
        }
    }

    private static EphemeralLlmCoordinator<Item, Result> Build(
        IScheduleCoordinator sched,
        IEphemeralPicker<Item> picker,
        IEphemeralLlmInvoker<Result> invoker,
        IEphemeralWriteback<Item, Result> writeback,
        Action<EphemeralLlmCoordinatorOptions>? configure = null)
    {
        var opts = new EphemeralLlmCoordinatorOptions();
        configure?.Invoke(opts);
        return new EphemeralLlmCoordinator<Item, Result>(
            picker, new PassthroughPrompter(), invoker, writeback,
            sched, Options.Create(opts), NullLogger<EphemeralLlmCoordinator<Item, Result>>.Instance);
    }

    [Fact]
    public async Task PicksAndProcessesItems_OnTickFire()
    {
        var sched = new FakeSchedule();
        var picker = new StaticPicker(new Item(1), new Item(2), new Item(3));
        var invoker = new CountingInvoker((p, _) => Task.FromResult(new Result(int.Parse(p.UserPrompt.Split('-')[1]), "ok")));
        var wb = new CollectingWriteback();

        using var c = Build(sched, picker, invoker, wb);
        await sched.FireAsync();

        Assert.Equal(3, invoker.Calls);
        Assert.Equal(3, wb.Written.Count);
    }

    [Fact]
    public async Task EmptyPickerYieldsNoInvocations()
    {
        var sched = new FakeSchedule();
        var invoker = new CountingInvoker((_, _) => Task.FromResult(new Result(0, "x")));
        var wb = new CollectingWriteback();

        using var c = Build(sched, new StaticPicker(), invoker, wb);
        await sched.FireAsync();

        Assert.Equal(0, invoker.Calls);
        Assert.Empty(wb.Written);
    }

    [Fact]
    public async Task FailedInvocationDoesNotWriteback()
    {
        var sched = new FakeSchedule();
        var invoker = new CountingInvoker((_, _) => throw new InvalidOperationException("boom"));
        var wb = new CollectingWriteback();

        using var c = Build(sched, new StaticPicker(new Item(7)), invoker, wb);
        await sched.FireAsync();

        Assert.Equal(1, invoker.Calls);
        Assert.Empty(wb.Written);
    }

    [Fact]
    public async Task ConcurrentInvocationsBoundedByMaxConcurrent()
    {
        var sched = new FakeSchedule();
        var inFlight = 0;
        var peakInFlight = 0;
        var gate = new TaskCompletionSource();
        var invoker = new CountingInvoker(async (_, _) =>
        {
            var v = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peakInFlight, v);
            await gate.Task;
            Interlocked.Decrement(ref inFlight);
            return new Result(0, "ok");
        });
        var wb = new CollectingWriteback();

        using var c = Build(sched, new StaticPicker(Enumerable.Range(0, 10).Select(i => new Item(i)).ToArray()),
                            invoker, wb, o => { o.MaxItemsPerTick = 10; o.MaxConcurrent = 2; });
        var fire = sched.FireAsync();
        await Task.Delay(50);
        Assert.True(peakInFlight <= 2, $"peakInFlight was {peakInFlight}");
        gate.SetResult();
        await fire;
    }

    [Fact]
    public async Task InvocationTimeoutCancelsLongCall()
    {
        var sched = new FakeSchedule();
        var observedCancellation = false;
        var invoker = new CountingInvoker(async (_, ct) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { observedCancellation = true; throw; }
            return new Result(0, "never");
        });
        var wb = new CollectingWriteback();

        using var c = Build(sched, new StaticPicker(new Item(1)), invoker, wb,
                            o => { o.InvocationTimeout = TimeSpan.FromMilliseconds(50); });
        await sched.FireAsync();
        Assert.True(observedCancellation);
        Assert.Empty(wb.Written);
    }

    [Fact]
    public async Task DisposeUnsubscribesFromSchedule()
    {
        var sched = new FakeSchedule();
        var c = Build(sched, new StaticPicker(new Item(1)),
                      new CountingInvoker((_, _) => Task.FromResult(new Result(0, "x"))),
                      new CollectingWriteback());
        Assert.NotNull(sched.Handler);
        c.Dispose();
        Assert.Null(sched.Handler);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PickerMaxIsHonored_EvenIfMoreAvailable()
    {
        var sched = new FakeSchedule();
        var picker = new StaticPicker(Enumerable.Range(0, 20).Select(i => new Item(i)).ToArray());
        var invoker = new CountingInvoker((_, _) => Task.FromResult(new Result(0, "ok")));
        var wb = new CollectingWriteback();

        using var c = Build(sched, picker, invoker, wb,
                            o => { o.MaxItemsPerTick = 5; o.MaxConcurrent = 5; });
        await sched.FireAsync();
        Assert.Equal(5, invoker.Calls);
        Assert.Equal(5, wb.Written.Count);
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int initial;
        do { initial = location; if (value <= initial) return; }
        while (Interlocked.CompareExchange(ref location, value, initial) != initial);
    }
}
