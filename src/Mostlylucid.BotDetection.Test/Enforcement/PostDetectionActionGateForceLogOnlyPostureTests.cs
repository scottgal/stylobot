using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Posture;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     2026-08-02 license-enforcement prerequisite: <see cref="IDetectionPostureProvider.ForceLogOnlyPosture"/>
///     must force every action-policy dispatch into the SAME observe-only shadow
///     <see cref="BotDetectionOptions.ObserveOnly"/> already drives
///     (<see cref="PostDetectionActionGate.MaybeShadowForObserveOnly"/>) -- reusing the existing
///     mechanism rather than adding a second, parallel shadow path.
/// </summary>
public sealed class PostDetectionActionGateForceLogOnlyPostureTests
{
    private sealed class FakePostureProvider : IDetectionPostureProvider
    {
        public bool LearningEnabled { get; init; } = true;
        public bool ForceLogOnlyPosture { get; init; }
    }

    private static PostDetectionActionGate Gate(IDetectionPostureProvider postureProvider) => new(
        Options.Create(new BotDetectionOptions
        {
            BotThreshold = 0.70,
            BotTypeActionPolicies = new Dictionary<string, string>
            {
                ["Tool"] = "throttle-tools"
            }
        }),
        NullLogger<PostDetectionActionGate>.Instance,
        postureProvider: postureProvider);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/some/path";
        return context;
    }

    private static AggregatedEvidence Evidence() => new()
    {
        BotProbability = 0.95,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.Tool,
        PrimaryBotName = "curl",
        Signals = new Dictionary<string, object>()
    };

    private sealed class RecordingRegistry : IActionPolicyRegistry
    {
        public List<string> Requested { get; } = new();

        public IActionPolicy? GetPolicy(string name)
        {
            Requested.Add(name);
            return new StubPolicy(name);
        }

        public IEnumerable<IActionPolicy> GetPoliciesByType(ActionType type) => Array.Empty<IActionPolicy>();
        public IReadOnlyDictionary<string, IActionPolicy> GetAllPolicies() => new Dictionary<string, IActionPolicy>();
        public void RegisterPolicy(IActionPolicy policy) { }
        public IActionPolicy GetDefaultPolicy(ActionType type) => new StubPolicy("default");
    }

    private sealed class StubPolicy : IActionPolicy
    {
        public StubPolicy(string name) => Name = name;
        public string Name { get; }
        public ActionType ActionType => ActionType.Throttle;

        public Task<ActionResult> ExecuteAsync(
            HttpContext context, AggregatedEvidence evidence, CancellationToken cancellationToken = default)
            => Task.FromResult(ActionResult.Allowed());
    }

    [Fact]
    public async Task ForceLogOnlyPosture_true_shadows_the_resolved_policy_into_logonly()
    {
        var gate = Gate(new FakePostureProvider { ForceLogOnlyPosture = true });
        var registry = new RecordingRegistry();

        var (outcome, evidence) = await gate.EvaluateAsync(Context(), Evidence(), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("throttle-tools", evidence.TriggeredActionPolicyName);
        // The registry was asked for "logonly" (the shadow), not "throttle-tools" directly --
        // MaybeShadowForObserveOnly swapped it in, same as when ObserveOnly=true does.
        Assert.Contains("logonly", registry.Requested);
    }

    [Fact]
    public async Task ForceLogOnlyPosture_false_resolves_the_normal_policy_unshadowed()
    {
        var gate = Gate(new FakePostureProvider { ForceLogOnlyPosture = false });
        var registry = new RecordingRegistry();

        var (outcome, evidence) = await gate.EvaluateAsync(Context(), Evidence(), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("throttle-tools", evidence.TriggeredActionPolicyName);
        Assert.DoesNotContain("logonly", registry.Requested);
    }
}
