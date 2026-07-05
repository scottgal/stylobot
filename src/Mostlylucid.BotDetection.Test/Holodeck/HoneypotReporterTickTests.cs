using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.ApiHolodeck.Services;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Learning;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Holodeck;

/// <summary>
///     Regression coverage for <see cref="HoneypotReporter"/>. After the
///     retirement of the legacy learning bus the reporter now hooks
///     <see cref="ILearningCoordinator.Signals"/>'s
///     <c>TypedSignalRaised</c> event in its constructor. Learning events fan
///     in synchronously via that callback (no per-tick channel drain); the
///     <see cref="TickCadence.Tick1m"/> subscription only processes the queued
///     report batch. These tests pin: (1) tick subscription shape, (2) tick
///     runs without a coordinator, (3) dispose releases the tick + signal
///     subscription, (4) raising a learning event on the coordinator's sink
///     lands in the queue.
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
        Mostlylucid.Ephemeral.TypedSignalSink<Mostlylucid.BotDetection.Events.LearningEvent>? learningSignals = null,
        HolodeckOptions? options = null)
    {
        return new HoneypotReporter(
            NullLogger<HoneypotReporter>.Instance,
            Options.Create(options ?? DefaultOptions()),
            learningSignals: learningSignals,
            scheduleCoordinator: coordinator);
    }

    private static Mostlylucid.Ephemeral.TypedSignalSink<Mostlylucid.BotDetection.Events.LearningEvent> NewLearningSink()
    {
        var inner = new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(1));
        return new Mostlylucid.Ephemeral.TypedSignalSink<Mostlylucid.BotDetection.Events.LearningEvent>(inner);
    }

    private static void Raise(
        Mostlylucid.Ephemeral.TypedSignalSink<Mostlylucid.BotDetection.Events.LearningEvent> sink,
        Mostlylucid.BotDetection.Events.LearningEvent evt)
    {
        var key = LearningSignalKeys.For(evt.Type);
        sink.Raise(key.Name, evt, key: evt.RequestId ?? evt.Pattern ?? evt.Source);
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
    public async Task OnTickAsync_runs_without_throwing_when_sink_is_null()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, learningSignals: null);

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
    public void Raising_high_confidence_event_queues_report()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sink = NewLearningSink();

        using var sut = NewService(coordinator, sink);

        Raise(sink, NewHighConfidenceEvent("203.0.113.10"));
        Raise(sink, NewHighConfidenceEvent("203.0.113.11"));

        // Signal-native fan-in classifies straight into the queue on the
        // TypedSignalRaised callback -- no tick required.
        sut.QueueSize.Should().Be(2);
    }

    [Fact]
    public async Task Reports_processed_on_tick_after_signal_fanin()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sink = NewLearningSink();

        using var sut = NewService(coordinator, sink);
        Raise(sink, NewHighConfidenceEvent("203.0.113.10"));
        Raise(sink, NewHighConfidenceEvent("203.0.113.11"));

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Both queued reports fit inside the tick budget (batch cap = 10).
        sut.QueueSize.Should().Be(0);
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
}