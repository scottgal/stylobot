using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 Category B regression coverage for the paired Cat-B migration of
///     <see cref="LearningBackgroundService"/> + <see cref="BoundedChannelLearningBus"/>.
///     <para>
///         Both were <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>s
///         whose <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on
///         a channel reader. Now both subscribe to <see cref="TickCadence.Tick1s"/>
///         and drain the channel via
///         <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/> per
///         tick.
///     </para>
///     <para>
///         Four facts per service (Cat-B template: subscribes, tick runs,
///         dispose releases, channel drains). The two services share state only
///         indirectly -- <see cref="LearningBackgroundService"/> consumes
///         <see cref="ILearningEventBus.Reader"/>, which the bus's wrapper
///         (<see cref="BoundedChannelLearningBus"/>) exposes as the inner bus's
///         reader. They do not share fields; this is Option X-paired (each is
///         independently tick-driven, no coupling beyond the existing DI graph).
///     </para>
/// </summary>
public sealed class LearningPairTickTests
{
    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static LearningEvent MakeEvent(LearningEventType type = LearningEventType.HighConfidenceDetection,
        string source = "test") => new()
    {
        Type = type,
        Source = source,
    };

    private static IOptions<BotDetectionOptions> OptionsFor(bool hpMode = false, int depth = 1_000)
    {
        var opts = new BotDetectionOptions();
        opts.SelfMaintenance.HighPerformanceMode = hpMode;
        opts.SelfMaintenance.LearningQueueDepth = depth;
        return Options.Create(opts);
    }

    /// <summary>
    ///     Captures every event the handler is invoked with so the drain test
    ///     can assert the events flowed through.
    /// </summary>
    private sealed class RecordingHandler : ILearningEventHandler
    {
        public List<LearningEvent> Received { get; } = new();

        public IReadOnlySet<LearningEventType> HandledEventTypes { get; } =
            new HashSet<LearningEventType>(Enum.GetValues<LearningEventType>());

        public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
        {
            Received.Add(evt);
            return Task.CompletedTask;
        }
    }

    // =====================================================================
    // BoundedChannelLearningBus -- four facts
    // =====================================================================

    [Fact]
    public void Bus_constructor_subscribes_to_Tick1s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);

        using var bus = new BoundedChannelLearningBus(
            inner,
            OptionsFor(hpMode: true),
            NullLogger<BoundedChannelLearningBus>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1s);
        sub.Name.Should().Be("BoundedChannelLearningBus");
        sub.Hint.Should().Be(CostHint.High);
    }

    [Fact]
    public async Task Bus_OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);

        using var bus = new BoundedChannelLearningBus(
            inner,
            OptionsFor(hpMode: true),
            NullLogger<BoundedChannelLearningBus>.Instance,
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        bus.FrontEndQueueDepth.Should().Be(0);
    }

    [Fact]
    public void Bus_dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);

        var bus = new BoundedChannelLearningBus(
            inner,
            OptionsFor(hpMode: true),
            NullLogger<BoundedChannelLearningBus>.Instance,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        bus.Dispose();

        sub.Disposed.Should().BeTrue();
        // Double-dispose safe.
        bus.Dispose();
    }

    [Fact]
    public async Task Bus_OnTickAsync_drains_front_end_channel_and_forwards_to_inner()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);

        using var bus = new BoundedChannelLearningBus(
            inner,
            OptionsFor(hpMode: true),
            NullLogger<BoundedChannelLearningBus>.Instance,
            coordinator);

        // Publish into the front-end channel (HP mode is on).
        bus.TryPublish(MakeEvent(source: "evt-a")).Should().BeTrue();
        bus.TryPublish(MakeEvent(source: "evt-b")).Should().BeTrue();
        bus.FrontEndQueueDepth.Should().Be(2);

        // Before the tick fires the inner bus has not seen the events.
        inner.Reader.TryRead(out _).Should().BeFalse();

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the tick drained the front-end channel
        // via TryRead and forwarded each event to the inner bus.
        bus.FrontEndQueueDepth.Should().Be(0);

        var forwarded = new List<string>();
        while (inner.Reader.TryRead(out var evt))
            forwarded.Add(evt.Source);
        forwarded.Should().BeEquivalentTo(["evt-a", "evt-b"]);
    }

    // =====================================================================
    // LearningBackgroundService -- four facts
    // =====================================================================

    [Fact]
    public void Service_constructor_subscribes_to_Tick1s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);
        var handlers = new ILearningEventHandler[] { new RecordingHandler() };

        using var sut = new LearningBackgroundService(
            inner,
            NullLogger<LearningBackgroundService>.Instance,
            OptionsFor(),
            handlers,
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1s);
        sub.Name.Should().Be("LearningBackgroundService");
        sub.Hint.Should().Be(CostHint.High);
    }

    [Fact]
    public async Task Service_OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);
        var handler = new RecordingHandler();

        using var sut = new LearningBackgroundService(
            inner,
            NullLogger<LearningBackgroundService>.Instance,
            OptionsFor(),
            new ILearningEventHandler[] { handler },
            coordinator);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        handler.Received.Should().BeEmpty();
    }

    [Fact]
    public void Service_dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);

        var sut = new LearningBackgroundService(
            inner,
            NullLogger<LearningBackgroundService>.Instance,
            OptionsFor(),
            new ILearningEventHandler[] { new RecordingHandler() },
            coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();
        // Double-dispose safe.
        sut.Dispose();
    }

    [Fact]
    public async Task Service_OnTickAsync_drains_bus_reader_and_dispatches_to_handlers()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var inner = new LearningEventBus(capacity: 100);
        var handler = new RecordingHandler();

        using var sut = new LearningBackgroundService(
            inner,
            NullLogger<LearningBackgroundService>.Instance,
            OptionsFor(),
            new ILearningEventHandler[] { handler },
            coordinator);

        // Publish two events on the bus -- the service should drain both on
        // the next tick.
        inner.TryPublish(MakeEvent(source: "lev-a")).Should().BeTrue();
        inner.TryPublish(MakeEvent(source: "lev-b")).Should().BeTrue();

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the tick drained the bus reader via
        // TryRead and dispatched both events through the recording handler.
        inner.Reader.TryRead(out _).Should().BeFalse();
        handler.Received.Select(e => e.Source).Should().BeEquivalentTo(["lev-a", "lev-b"]);
    }
}
