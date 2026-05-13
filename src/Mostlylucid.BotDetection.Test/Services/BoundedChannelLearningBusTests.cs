using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Unit tests for <see cref="BoundedChannelLearningBus"/>.
/// </summary>
public class BoundedChannelLearningBusTests : IAsyncDisposable
{
    private readonly List<BoundedChannelLearningBus> _busesToDispose = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var bus in _busesToDispose)
        {
            bus.Complete();
            await bus.StopAsync(CancellationToken.None);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static LearningEvent MakeEvent(string source = "test") => new()
    {
        Type = LearningEventType.HighConfidenceDetection,
        Source = source,
    };

    private static IOptions<BotDetectionOptions> OptionsFor(bool hpMode, int depth = 1_000)
    {
        var opts = new BotDetectionOptions();
        opts.SelfMaintenance.HighPerformanceMode = hpMode;
        opts.SelfMaintenance.LearningQueueDepth = depth;
        return Options.Create(opts);
    }

    private BoundedChannelLearningBus CreateBus(bool hpMode, int depth = 1_000,
        LearningEventBus? inner = null)
    {
        inner ??= new LearningEventBus(capacity: 10_000);
        var bus = new BoundedChannelLearningBus(
            inner,
            OptionsFor(hpMode, depth),
            NullLogger<BoundedChannelLearningBus>.Instance);
        _busesToDispose.Add(bus);
        return bus;
    }

    // -----------------------------------------------------------------------
    // Test 1: HP mode OFF — TryPublish delegates directly to the inner bus
    // -----------------------------------------------------------------------

    [Fact]
    public void TryPublish_WhenHpModeOff_InvokesInnerBusDirectly()
    {
        // Arrange
        var inner = new LearningEventBus(capacity: 10_000);
        var bus = CreateBus(hpMode: false, inner: inner);
        var evt = MakeEvent();

        // Act
        var result = bus.TryPublish(evt);

        // Assert — event appears immediately on the inner bus reader
        Assert.True(result);
        Assert.True(inner.Reader.TryRead(out var received));
        Assert.Equal(evt.Source, received!.Source);

        inner.Complete();
    }

    // -----------------------------------------------------------------------
    // Test 2: HP mode ON — TryPublish returns before the inner bus receives
    //         the event; the background consumer delivers it asynchronously.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryPublish_WhenHpModeOn_ReturnsImmediately_InnerBusReceivesLater()
    {
        // Arrange
        var inner = new LearningEventBus(capacity: 10_000);
        var bus = CreateBus(hpMode: true, inner: inner);
        var evt = MakeEvent("hp-source");

        // Start the background consumer. Use a generous timeout so this is not flaky
        // on slow CI runners; the actual forwarding happens in microseconds when the
        // scheduler is responsive.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bus.StartAsync(cts.Token);

        // Act: TryPublish returns before the consumer forwards the event.
        var result = bus.TryPublish(evt);
        Assert.True(result);

        // Wait directly on the inner reader. WaitToReadAsync is the canonical
        // channel-aware wait; no Task.Run + TaskCompletionSource shim needed.
        var hasData = await inner.Reader.WaitToReadAsync(cts.Token);
        Assert.True(hasData, "Inner reader signalled completion before delivering the event");
        Assert.True(inner.Reader.TryRead(out var delivered),
            "WaitToReadAsync returned true but TryRead found no item");

        Assert.Equal(evt.Source, delivered!.Source);
        Assert.Equal(evt.Type, delivered.Type);
    }

    // -----------------------------------------------------------------------
    // Test 3: HP mode ON with tiny queue — filling it and adding one more
    //         drops the oldest entry; new entry is still accepted.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryPublish_WhenQueueFull_DropsOldestAndAcceptsNew()
    {
        // Arrange: front-end channel depth = 2 so it's easy to fill
        const int depth = 2;
        var inner = new LearningEventBus(capacity: 10_000);
        var bus = CreateBus(hpMode: true, depth: depth, inner: inner);

        // Do NOT start the background consumer — keeps the front-end channel full.

        // Fill the queue to capacity
        var first = MakeEvent("first");
        var second = MakeEvent("second");
        Assert.True(bus.TryPublish(first));
        Assert.True(bus.TryPublish(second));

        // Act: add a third event — DropOldest silently discards "first",
        //      TryWrite still returns true (channel accepted the new item).
        var third = MakeEvent("third");
        var result = bus.TryPublish(third);

        // Assert: TryPublish returns true (channel accepted the write)
        Assert.True(result);

        // Now start the consumer and let it drain into the inner bus. Use a generous
        // deadline so this is not flaky on slow CI runners; the actual drain is
        // microseconds when the scheduler is responsive.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await bus.StartAsync(cts.Token);

        var received = new List<string>();
        for (var i = 0; i < depth; i++)
        {
            if (!await inner.Reader.WaitToReadAsync(cts.Token)) break;
            if (inner.Reader.TryRead(out var evt))
                received.Add(evt.Source);
        }

        // "first" should have been dropped (oldest); "second" and "third" survive
        Assert.DoesNotContain("first", received);
        Assert.Contains("second", received);
        Assert.Contains("third", received);

        inner.Complete();
    }

    // -----------------------------------------------------------------------
    // Test 4: Reader and Subscribe delegate to the inner bus, not the front-end channel
    // -----------------------------------------------------------------------

    [Fact]
    public void Reader_DelegatesToInnerBus()
    {
        var inner = new LearningEventBus(capacity: 10_000);
        var bus = CreateBus(hpMode: true, inner: inner);

        // The Reader exposed by the wrapper is the inner bus's reader
        Assert.Same(inner.Reader, bus.Reader);

        inner.Complete();
    }

    // -----------------------------------------------------------------------
    // Test 5: LowMemory preset activates HP mode with 500-entry queue
    // -----------------------------------------------------------------------

    [Fact]
    public void LowMemoryPreset_SetsHighPerformanceModeAndReducedQueueDepth()
    {
        var sm = SelfMaintenanceOptions.LowMemory;
        Assert.True(sm.HighPerformanceMode);
        Assert.Equal(500, sm.LearningQueueDepth);
    }
}
