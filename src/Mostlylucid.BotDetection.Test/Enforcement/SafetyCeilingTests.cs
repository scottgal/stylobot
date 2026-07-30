using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     Guards the site-wide safety ceiling (<see cref="BotDetectionOptions.SafetyCeilingRpm"/>)
///     in <see cref="PostDetectionActionGate"/>. Every benign-routing carve-out
///     (verified-crawler fast path, corroborated registry client, recognized
///     webhook sender) and the plain no-override path let requests through
///     WITHOUT shaping so legitimate high-volume automation is never
///     throttled -- but an absolute flood, even of that trusted/recognized
///     traffic, must still be shed. The ceiling is the only thing allowed to
///     shape those paths, and only once the per-(visitor, endpoint) token
///     bucket is exhausted.
/// </summary>
public sealed class SafetyCeilingTests
{
    [Fact]
    public async Task Recognized_traffic_below_ceiling_is_never_shaped_but_flood_is_shed()
    {
        var store = new InMemoryTokenBucketStore();
        var gate = Gate(safetyCeilingRpm: 5, store);
        var registry = new RecordingRegistry();

        for (var i = 0; i < 5; i++)
        {
            var context = Context();
            var (outcome, evidence) = await gate.EvaluateAsync(
                context, WebhookEvidence(), registry);

            Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
            Assert.Equal("webhook-recognized", evidence.TriggeredActionPolicyName);
            Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        }

        // 6th request in the same window exhausts the ceiling bucket.
        var floodContext = Context();
        var (floodOutcome, _) = await gate.EvaluateAsync(
            floodContext, WebhookEvidence(), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyHandledResponse, floodOutcome);
        Assert.Equal(StatusCodes.Status429TooManyRequests, floodContext.Response.StatusCode);
        Assert.True(floodContext.Response.Headers.ContainsKey("Retry-After"));
    }

    [Fact]
    public async Task Ceiling_applies_site_wide_to_non_webhook_endpoint_too()
    {
        var store = new InMemoryTokenBucketStore();
        var gate = Gate(safetyCeilingRpm: 5, store);
        var registry = new RecordingRegistry();

        for (var i = 0; i < 5; i++)
        {
            var context = PlainContext();
            var (outcome, _) = await gate.EvaluateAsync(
                context, PlainEvidence(), registry);

            Assert.Equal(PostDetectionActionOutcome.NoOverride, outcome);
        }

        var floodContext = PlainContext();
        var (floodOutcome, _) = await gate.EvaluateAsync(
            floodContext, PlainEvidence(), registry);

        Assert.Equal(PostDetectionActionOutcome.PolicyHandledResponse, floodOutcome);
        Assert.Equal(StatusCodes.Status429TooManyRequests, floodContext.Response.StatusCode);
    }

    [Fact]
    public async Task Zero_ceiling_disables_enforcement()
    {
        var store = new InMemoryTokenBucketStore();
        var gate = Gate(safetyCeilingRpm: 0, store);
        var registry = new RecordingRegistry();

        for (var i = 0; i < 20; i++)
        {
            var context = PlainContext();
            var (outcome, _) = await gate.EvaluateAsync(context, PlainEvidence(), registry);
            Assert.Equal(PostDetectionActionOutcome.NoOverride, outcome);
        }
    }

    private static PostDetectionActionGate Gate(int safetyCeilingRpm, ITokenBucketStore store) => new(
        Options.Create(new BotDetectionOptions
        {
            BotThreshold = 0.70,
            SafetyCeilingRpm = safetyCeilingRpm,
            BotTypeActionPolicies = new Dictionary<string, string>
            {
                ["GoodBot"] = "throttle-status"
            }
        }),
        NullLogger<PostDetectionActionGate>.Instance,
        store);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/webhooks/stripe";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.10");
        return context;
    }

    private static DefaultHttpContext PlainContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/blog/post-1";
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.20");
        return context;
    }

    // A recognized webhook sender: corroborated, so the webhook-recognized benign
    // arm fires and would otherwise let every request through unshaped.
    private static AggregatedEvidence WebhookEvidence() => new()
    {
        BotProbability = 0.72,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.GoodBot,
        PrimaryBotName = "Stripe Webhook",
        WebhookRecognized = true,
        Signals = new Dictionary<string, object>()
    };

    // Plain human/unclassified traffic that never triggers any action policy --
    // proves the ceiling is a site-wide backstop, not webhook-specific.
    private static AggregatedEvidence PlainEvidence() => new()
    {
        BotProbability = 0.05,
        Confidence = 1.0,
        RiskBand = RiskBand.Low,
        PrimaryBotType = null,
        Signals = new Dictionary<string, object>()
    };

    /// <summary>
    ///     Minimal <see cref="IActionPolicyRegistry"/> that records which policy
    ///     names were requested and hands back a no-op continue policy.
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
