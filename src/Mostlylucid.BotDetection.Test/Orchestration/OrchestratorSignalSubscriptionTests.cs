using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Orchestration;

public class OrchestratorSignalSubscriptionTests
{
    public static IEnumerable<object[]> Orchestrators() => new[]
    {
        new object[] { "Ephemeral", (Func<IDetectionOrchestrator>)CreateEphemeral },
        new object[] { "Blackboard", (Func<IDetectionOrchestrator>)CreateBlackboard }
    };

    private static IDetectionOrchestrator CreateEphemeral()
    {
        return new EphemeralDetectionOrchestrator(
            NullLogger<EphemeralDetectionOrchestrator>.Instance,
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IContributingDetector>());
    }

    private static IDetectionOrchestrator CreateBlackboard()
    {
        // PiiHasher requires a >=16 byte key. Everything else is nullable/optional.
        var hasher = new PiiHasher(new byte[16]);
        return new BlackboardOrchestrator(
            NullLogger<BlackboardOrchestrator>.Instance,
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IContributingDetector>(),
            hasher);
    }

    [Theory]
    [MemberData(nameof(Orchestrators))]
    public void SubscribeToSignals_receives_raised_signals(string name, Func<IDetectionOrchestrator> factory)
    {
        _ = name;
        var orchestrator = factory();

        var received = new List<SignalEvent>();
        using var sub = orchestrator.SubscribeToSignals(received.Add);

        orchestrator.RaiseSignalForObservability("test.observed", key: "k1");

        received.Should().ContainSingle(s => s.Signal == "test.observed" && s.Key == "k1");
    }

    [Theory]
    [MemberData(nameof(Orchestrators))]
    public void Disposing_subscription_stops_delivery(string name, Func<IDetectionOrchestrator> factory)
    {
        _ = name;
        var orchestrator = factory();

        var received = new List<SignalEvent>();
        var sub = orchestrator.SubscribeToSignals(received.Add);
        sub.Dispose();

        orchestrator.RaiseSignalForObservability("after.dispose");

        received.Should().BeEmpty();
    }

    [Fact]
    public void Two_orchestrator_instances_do_not_share_signals()
    {
        var a = CreateEphemeral();
        var b = CreateBlackboard();

        var receivedA = new List<SignalEvent>();
        var receivedB = new List<SignalEvent>();
        using var subA = a.SubscribeToSignals(receivedA.Add);
        using var subB = b.SubscribeToSignals(receivedB.Add);

        a.RaiseSignalForObservability("only.on.a");

        receivedA.Should().ContainSingle(s => s.Signal == "only.on.a");
        receivedB.Should().BeEmpty();
    }
}