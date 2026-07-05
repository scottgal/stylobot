using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins the escalate-target dispatch shape:
///     <c>options["Target"] = learning|session|llm</c> selects the concrete
///     <see cref="IActionPolicy"/>; unknown target throws; no target defaults
///     to learning. Also pins the type-check on
///     <see cref="IActionPolicy.ActionType"/>: all three return
///     <see cref="ActionType.Escalate"/> so the registry indexes them under
///     the same bucket.
/// </summary>
public class EscalateActionPolicyFactoryTests
{
    private static EscalateActionPolicyFactory NewFactory(
        SessionStore? sessionStore = null,
        TypedSignalSink<LlmClassificationRequest>? llmSink = null)
    {
        var learningSink = new TypedSignalSink<LearningEvent>(
            new SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromMinutes(5)));
        return new EscalateActionPolicyFactory(
            learningSignals: learningSink,
            sessionStore: sessionStore,
            siteProfiles: null,
            llmRequestSignals: llmSink);
    }

    private static SessionStore NewSessionStore()
    {
        var opts = Options.Create(new SessionStoreOptions());
        return new SessionStore(
            opts,
            NullLogger<SessionStore>.Instance);
    }

    [Fact]
    public void Factory_ActionType_is_Escalate()
    {
        NewFactory().ActionType.Should().Be(ActionType.Escalate);
    }

    [Fact]
    public void No_target_defaults_to_learning()
    {
        var policy = NewFactory().Create("my-esc", new Dictionary<string, object>());
        policy.Should().BeOfType<EscalateToLearningActionPolicy>();
        policy.Name.Should().Be("my-esc");
        policy.ActionType.Should().Be(ActionType.Escalate);
    }

    [Fact]
    public void Target_learning_parses_options()
    {
        var policy = NewFactory().Create("named", new Dictionary<string, object>
        {
            ["Target"] = "learning",
            ["EventType"] = "InferenceRequest",
            ["MinBotProbability"] = 0.4,
            ["MinConfidence"] = 0.6,
            ["IncludeFeatureVector"] = true,
        });
        policy.Should().BeOfType<EscalateToLearningActionPolicy>();
    }

    [Fact]
    public void Target_session_requires_registered_SessionStore()
    {
        var act = () => NewFactory(sessionStore: null).Create("s", new Dictionary<string, object>
        {
            ["Target"] = "session",
        });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SessionStore is registered*",
                "the factory must fail loudly when a session-target policy is configured " +
                "but the host hasn't registered a SessionStore — silently no-oping would " +
                "make it look like escalation is working when it isn't");
    }

    [Fact]
    public void Target_session_builds_with_registered_store()
    {
        var store = NewSessionStore();
        var policy = NewFactory(sessionStore: store).Create("s", new Dictionary<string, object>
        {
            ["Target"] = "session",
            ["MinBotProbability"] = 0.5,
        });
        policy.Should().BeOfType<EscalateToSessionActionPolicy>();
    }

    [Fact]
    public void Target_llm_builds_without_sink_registered()
    {
        // LLM sink is optional -- the coordinator no-ops on drain when no
        // provider is configured, so the factory must not require the sink.
        var policy = NewFactory(llmSink: null).Create("l", new Dictionary<string, object>
        {
            ["Target"] = "llm",
            ["EnqueueReason"] = "test",
            ["MinBotProbability"] = 0.2,
            ["MaxBotProbability"] = 0.9,
            ["IsDriftSample"] = false,
        });
        policy.Should().BeOfType<EscalateToLlmActionPolicy>();
    }

    [Fact]
    public void Unknown_target_throws()
    {
        var act = () => NewFactory().Create("bad", new Dictionary<string, object>
        {
            ["Target"] = "outer-space",
        });
        act.Should().Throw<ArgumentException>()
            .WithMessage("*outer-space*");
    }

    [Fact]
    public void Target_lookup_is_case_insensitive()
    {
        // YAML config authors will write "Learning" / "LLM" freely; the
        // factory must match those to the lowercase target strings.
        var policy = NewFactory().Create("mixed", new Dictionary<string, object>
        {
            ["Target"] = "LEARNING",
        });
        policy.Should().BeOfType<EscalateToLearningActionPolicy>();
    }
}
