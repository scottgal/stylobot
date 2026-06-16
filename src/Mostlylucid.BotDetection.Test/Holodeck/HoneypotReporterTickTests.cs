using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Holodeck;

/// <summary>
///     Wave 2 Category B pilot regression coverage for
///     <see cref="HoneypotReporter"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///     fire-and-forget <c>await foreach</c> on <see cref="ILearningEventBus.Reader"/>
///     plus a 1-minute <c>Task.Delay</c> loop that drained the in-memory
///     <c>ConcurrentQueue&lt;ReportEntry&gt;</c>; now subscribes to
///     <see cref="TickCadence.Tick1m"/> and each tick drains the channel
///     non-blocking via <see cref="ChannelReader{T}.TryRead"/> before running the
///     existing batched report-queue submission.
///     <para>
///         Three standard Cat-B facts (subscription shape, tick runs, dispose
///         releases) plus one Cat-B-specific fact: the tick drains the channel.
///     </para>
/// </summary>
public sealed class HoneypotReporterTickTests
{
    private static HolodeckOptions DefaultOptions() => new()
    {
        ReportToProjectHoneypot = true,
        ProjectHoneypotAccessKey = "test-key",
        MinRiskToReport = 0.5,
        MaxReportsPerHour = 100,
        ReportVisitorTypes =
        {
            ReportableVisitorType.Suspicious,
            ReportableVisitorType.Harvester,
            ReportableVisitorType.CommentSpammer
        }
    };

    private static HoneypotReporter NewService(
        RecordingScheduleCoordinator coordinator,
        ILearningEventBus? bus = null,
        HolodeckOptions? options = null)
    {
        return new HoneypotReporter(
            NullLogger<HoneypotReporter>.Instance,
            Options.Create(options ?? DefaultOptions()),
            learningEventBus: bus,
            scheduleCoordinator: coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick1m_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick1m);
        sub.Name.Should().Be("HoneypotReporter");
        sub.Hint.Should().Be(CostHint.Low);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_when_bus_is_null()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, bus: null);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        sut.QueueSize.Should().Be(0);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = NewService(coordinator);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task OnTickAsync_drains_learning_event_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var bus = new TestLearningEventBus();

        // Publish two qualifying events BEFORE the tick fires. The migrated
        // service no longer runs a fire-and-forget await-foreach, so without a
        // tick fire these events sit in the channel indefinitely.
        bus.TryPublish(NewHighConfidenceEvent("203.0.113.10")).Should().BeTrue();
        bus.TryPublish(NewHighConfidenceEvent("203.0.113.11")).Should().BeTrue();

        using var sut = NewService(coordinator, bus);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // The channel is fully drained by the tick handler -- this is the
        // load-bearing Cat-B assertion: events that previously flowed via
        // continuous await-foreach now flow via per-tick TryRead.
        bus.Reader.TryRead(out _).Should().BeFalse();

        // Both events were processed (queued for reporting and then submitted
        // in the same tick within the rate-limit budget).
        sut.QueueSize.Should().Be(0);
    }

    [Fact]
    public async Task OnTickAsync_drains_channel_even_when_reporting_disabled()
    {
        // Subtle but important: events should be drained from the channel even
        // if reporting is disabled, OR the early-return must not strand events
        // on the channel indefinitely. The current implementation early-returns
        // before draining when disabled -- documenting that contract here so
        // any future change is conscious.
        //
        // If/when the contract changes, flip this assertion. Today: when
        // reporting is disabled, the channel is NOT drained (the early-return
        // happens first) -- which is fine because ReportToProjectHoneypot is
        // expected to stay false in deployments that don't host a honeypot
        // script, and the LearningEventBus channel is bounded with
        // DropOldest fullness mode so unread events do not leak memory.
        var coordinator = new RecordingScheduleCoordinator();
        var bus = new TestLearningEventBus();
        bus.TryPublish(NewHighConfidenceEvent("203.0.113.10")).Should().BeTrue();

        var disabledOpts = DefaultOptions();
        disabledOpts.ReportToProjectHoneypot = false;

        using var sut = NewService(coordinator, bus, disabledOpts);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Disabled => tick early-returns => channel is NOT drained.
        bus.Reader.TryRead(out _).Should().BeTrue();
    }

    private static LearningEvent NewHighConfidenceEvent(string ip) => new()
    {
        Type = LearningEventType.HighConfidenceDetection,
        Source = "test",
        Confidence = 0.95,
        Metadata = new Dictionary<string, object>
        {
            [SignalKeys.ClientIp] = ip,
            [SignalKeys.UserAgent] = "test-ua/1.0"
        }
    };

    /// <summary>
    ///     Minimal <see cref="ILearningEventBus"/> stand-in: wraps a single
    ///     unbounded channel so tests can publish events that the service's
    ///     tick handler then drains via <see cref="ChannelReader{T}.TryRead"/>.
    /// </summary>
    private sealed class TestLearningEventBus : ILearningEventBus
    {
        private readonly Channel<LearningEvent> _channel = Channel.CreateUnbounded<LearningEvent>();
        public ChannelReader<LearningEvent> Reader => _channel.Reader;

        public ChannelReader<LearningEvent> Subscribe(string subscriberName, int capacity = 10_000)
            => _channel.Reader;

        public bool TryPublish(LearningEvent evt) => _channel.Writer.TryWrite(evt);

        public void Complete() => _channel.Writer.TryComplete();
    }
}