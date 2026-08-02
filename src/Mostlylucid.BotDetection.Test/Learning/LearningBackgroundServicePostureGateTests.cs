using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Posture;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Learning;

/// <summary>
///     2026-08-02 license-enforcement prerequisite: <see cref="IDetectionPostureProvider.LearningEnabled"/>
///     must also suppress the SEPARATE heuristic-weight learning pipeline
///     (<see cref="ILearningEventHandler"/> / <see cref="LearningBackgroundService"/>), not just the
///     identity/fingerprint absorption path. Rather than gating each of the several per-request
///     producers individually (EscalateToLearningActionPolicy, IntentClassificationCoordinator,
///     LlmClassificationCoordinator, DriftDetectionHandler...), this gates the SINGLE dispatch choke
///     point every producer already funnels through -- covers all current AND future producers.
/// </summary>
public sealed class LearningBackgroundServicePostureGateTests
{
    private sealed class FakePostureProvider : IDetectionPostureProvider
    {
        public bool LearningEnabled { get; init; } = true;
        public bool ForceLogOnlyPosture { get; init; }
    }

    private sealed class RecordingHandler : ILearningEventHandler
    {
        public List<LearningEvent> Handled { get; } = new();

        public IReadOnlySet<LearningEventType> HandledEventTypes { get; } =
            new HashSet<LearningEventType> { LearningEventType.HighConfidenceDetection };

        public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
        {
            Handled.Add(evt);
            return Task.CompletedTask;
        }
    }

    private static TypedSignalSink<LearningEvent> NewSink()
    {
        var inner = new SignalSink(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
        return new TypedSignalSink<LearningEvent>(inner, maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));
    }

    private static LearningEvent Event() => new()
    {
        Type = LearningEventType.HighConfidenceDetection,
        Source = "test",
    };

    [Fact]
    public async Task LearningEnabled_false_suppresses_dispatch_to_handlers()
    {
        var handler = new RecordingHandler();
        var service = new LearningBackgroundService(
            NewSink(), NullLogger<LearningBackgroundService>.Instance,
            Options.Create(new BotDetectionOptions()), new[] { handler },
            new FakePostureProvider { LearningEnabled = false });

        await service.ProcessEventAsync(Event(), CancellationToken.None);

        Assert.Empty(handler.Handled);
    }

    [Fact]
    public async Task LearningEnabled_true_still_dispatches_to_handlers()
    {
        var handler = new RecordingHandler();
        var service = new LearningBackgroundService(
            NewSink(), NullLogger<LearningBackgroundService>.Instance,
            Options.Create(new BotDetectionOptions()), new[] { handler },
            new FakePostureProvider { LearningEnabled = true });

        await service.ProcessEventAsync(Event(), CancellationToken.None);

        Assert.Single(handler.Handled);
    }
}
