using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class OrchestratorSignalSubscriptionTests
{
    [Fact]
    public void SubscribeToSignals_receives_raised_signals()
    {
        var options = Options.Create(new BotDetectionOptions());
        var orchestrator = new EphemeralDetectionOrchestrator(
            NullLogger<EphemeralDetectionOrchestrator>.Instance,
            options,
            Array.Empty<IContributingDetector>());

        var received = new List<SignalEvent>();
        using var sub = orchestrator.SubscribeToSignals(received.Add);

        orchestrator.RaiseSignalForObservability("test.observed", key: "k1");

        received.Should().ContainSingle(s => s.Signal == "test.observed" && s.Key == "k1");
    }

    [Fact]
    public void Disposing_subscription_stops_delivery()
    {
        var options = Options.Create(new BotDetectionOptions());
        var orchestrator = new EphemeralDetectionOrchestrator(
            NullLogger<EphemeralDetectionOrchestrator>.Instance,
            options,
            Array.Empty<IContributingDetector>());

        var received = new List<SignalEvent>();
        var sub = orchestrator.SubscribeToSignals(received.Add);
        sub.Dispose();

        orchestrator.RaiseSignalForObservability("after.dispose");

        received.Should().BeEmpty();
    }
}