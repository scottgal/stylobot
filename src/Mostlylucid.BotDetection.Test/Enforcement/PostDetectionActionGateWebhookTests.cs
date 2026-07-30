using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     Guards the webhook-recognition benign-routing fix in
///     <see cref="PostDetectionActionGate"/>. A RECOGNIZED webhook receiver
///     (a corroborated webhook sender hitting its configured receiver endpoint)
///     must NOT be routed through the normal BotType throttle bucket, while an
///     unrecognized request to the same endpoint still must -- proving the
///     carve-out keys on the corroboration flag, not on path or BotType alone
///     (which would be a bypass).
/// </summary>
public sealed class PostDetectionActionGateWebhookTests
{
    [Fact]
    public async Task Recognized_webhook_is_not_throttled()
    {
        var gate = Gate();
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, WebhookEvidence(webhookRecognized: true), registry);

        // Benign routing: the pipeline continues and no throttle policy is invoked.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal("webhook-recognized", evidence.TriggeredActionPolicyName);

        // The GoodBot throttle bucket was never consulted, and no 429 was shaped.
        Assert.DoesNotContain("throttle-status", registry.Requested);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
    }

    [Fact]
    public async Task Unrecognized_traffic_to_same_endpoint_still_gets_normal_action()
    {
        var gate = Gate();
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, WebhookEvidence(webhookRecognized: false), registry);

        // Unrecognized traffic shaped as a GoodBot over threshold still resolves
        // GoodBot -> throttle-status (proof no path bypass).
        Assert.Contains("throttle-status", registry.Requested);
        Assert.Equal("throttle-status", evidence.TriggeredActionPolicyName);
        // The stub policy allowed continuation; the point is that resolution happened.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    private static PostDetectionActionGate Gate() => new(
        Options.Create(new BotDetectionOptions
        {
            BotThreshold = 0.70,
            BotTypeActionPolicies = new Dictionary<string, string>
            {
                ["GoodBot"] = "throttle-status"
            }
        }),
        NullLogger<PostDetectionActionGate>.Instance);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/webhooks/stripe";
        return context;
    }

    // A machine webhook sender: BotType.GoodBot, probability above the bot threshold.
    // webhookRecognized toggles the corroboration flag that WebhookSensor sets only
    // when the request is corroborated as a recognized webhook sender.
    private static AggregatedEvidence WebhookEvidence(bool webhookRecognized) => new()
    {
        BotProbability = 0.72,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.GoodBot,
        PrimaryBotName = "Stripe Webhook",
        WebhookRecognized = webhookRecognized,
        Signals = new Dictionary<string, object>()
    };

    /// <summary>
    ///     Minimal <see cref="IActionPolicyRegistry"/> that records which policy
    ///     names were requested and hands back a no-op continue policy, so a test
    ///     can assert on routing WITHOUT executing the real throttle-status action.
    /// </summary>
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
}
