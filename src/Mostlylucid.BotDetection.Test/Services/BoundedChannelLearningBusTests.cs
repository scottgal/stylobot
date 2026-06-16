using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Unit tests for <see cref="BoundedChannelLearningBus"/>. Post-Wave-2:
///     the bus is a plain singleton that drains its HP-mode front-end channel
///     on a ScheduleCoordinator Tick1s. These tests construct the bus without
///     a coordinator and drive the migrated <see cref="BoundedChannelLearningBus.OnTickAsync"/>
///     handler directly to assert the drain semantics that the old
///     <c>ExecuteAsync</c> loop used to provide.
/// </summary>
public class BoundedChannelLearningBusTests : IAsyncDisposable
{
    private readonly List<BoundedChannelLearningBus> _busesToDispose = new();

    public ValueTask DisposeAsync()
    {
        foreach (var bus in _busesToDispose)
        {
            try { bus.Complete(); } catch { /* already torn down */ }
            try { bus.Dispose(); } catch { /* already torn down */ }
        }
        return ValueTask.CompletedTask;
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
    // Test 2: HP mode ON — TryPublish writes the event to the front-end
    //         channel; OnTickAsync forwards it to the inner bus.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task TryPublish_WhenHpModeOn_FrontEndChannel_DrainedOnTick()
    {
        // Arrange
        var inner = new LearningEventBus(capacity: 10_000);
        var bus = CreateBus(hpMode: true, inner: inner);
        var evt = MakeEvent("hp-source");

        // Act: TryPublish writes to the front-end channel; inner bus does not
        // see the event until the tick handler runs.
        var result = bus.TryPublish(evt);
        Assert.True(result);

        // Before the tick fires, the inner bus has no event.
        Assert.False(inner.Reader.TryRead(out _));

        // Drive the tick handler directly -- ScheduleCoordinator is not wired
        // in these unit tests; we call the public OnTickAsync as a test seam.
        await bus.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // After the tick the event is on the inner bus.
        Assert.True(inner.Reader.TryRead(out var delivered));
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

        // Drive the tick to drain the front-end channel into the inner bus.
        await bus.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var received = new List<string>();
        for (var i = 0; i < depth; i++)
        {
            if (inner.Reader.TryRead(out var evt))
                received.Add(evt.Source);
            else
                break;
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
