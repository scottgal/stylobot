using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Test.Enforcement;

/// <summary>
///     Guards the registry-client benign-routing fix in
///     <see cref="PostDetectionActionGate"/>. A CORROBORATED registry client
///     (docker/buildx push through the gateway) must NOT be routed through the
///     Tool -> throttle-tools HTTP 429 bucket, while a plain Tool (curl) still
///     must -- proving the carve-out keys on the spoof-guarded corroboration
///     flag, not on <see cref="BotType.Tool"/> alone (which would be a bypass).
/// </summary>
public sealed class PostDetectionActionGateRegistryTests
{
    [Fact]
    public async Task Corroborated_registry_client_is_not_throttled()
    {
        var gate = Gate();
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, ToolEvidence(registryClientCorroborated: true), registry);

        // Benign routing: the pipeline continues and no throttle policy is invoked.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
        Assert.Equal(PostDetectionActionGate.RegistryClientFastPathName, evidence.TriggeredActionPolicyName);
        Assert.True(context.Items.ContainsKey("BotDetection.RegistryClientRecognized"));

        // The Tool throttle bucket was never consulted, and no 429 was shaped.
        Assert.DoesNotContain("throttle-tools", registry.Requested);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
    }

    [Fact]
    public async Task Uncorroborated_tool_still_routes_through_throttle_tools()
    {
        var gate = Gate();
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, ToolEvidence(registryClientCorroborated: false), registry);

        // A plain curl/python Tool over threshold resolves Tool -> throttle-tools.
        Assert.Contains("throttle-tools", registry.Requested);
        Assert.Equal("throttle-tools", evidence.TriggeredActionPolicyName);
        // The stub policy allowed continuation; the point is that resolution happened.
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    [Fact]
    public async Task Internal_class_wins_over_endpoint_rate_limit_rule()
    {
        // P0 2026-08-16 (the operator's 429 whole-page break): the site→gateway
        // dashboard reads (/api/v1/*) carried a path-scoped rate-limit endpoint
        // rule — the endpoint override set TriggeredActionPolicyName and the
        // Internal class fallback never ran, so the product's own plumbing got
        // throttled (the 60/min bucket exhausted → 429s → the dashboard's Warming
        // sentinel pages). The Internal class's policy (logonly/allow) must be the
        // effective action for Internal-classified traffic regardless of the
        // endpoint rule; the honeypot arm still wins (behaviour beats identity).
        var gate = Gate();
        var registry = new RecordingRegistry();
        var context = Context();

        var (outcome, evidence) = await gate.EvaluateAsync(
            context, InternalEvidence(), registry);

        Assert.Equal("logonly", evidence.TriggeredActionPolicyName);
        Assert.DoesNotContain("throttle-tools", registry.Requested);
        Assert.Equal(PostDetectionActionOutcome.PolicyContinued, outcome);
    }

    private static AggregatedEvidence InternalEvidence() => new()
    {
        BotProbability = 1.0,
        Confidence = 1.0,
        RiskBand = RiskBand.Low,
        PrimaryBotType = BotType.Internal,
        PrimaryBotName = "StyloBot dashboard host",
        Signals = new Dictionary<string, object>()
    };

    private static PostDetectionActionGate Gate() => new(
        Options.Create(new BotDetectionOptions
        {
            BotThreshold = 0.70,
            BotTypeActionPolicies = new Dictionary<string, string>
            {
                ["Tool"] = "throttle-tools",
                ["Internal"] = "logonly"
            }
        }),
        NullLogger<PostDetectionActionGate>.Instance);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/v2/library/alpine/manifests/latest";
        return context;
    }

    // A machine registry client: BotType.Tool, probability above the bot threshold.
    // registryClientCorroborated toggles the spoof-guarded corroboration flag that
    // RegistryClientSensor sets only when the OCI /v2 protocol behaviour is proven.
    private static AggregatedEvidence ToolEvidence(bool registryClientCorroborated) => new()
    {
        BotProbability = 0.72,
        Confidence = 1.0,
        RiskBand = RiskBand.High,
        PrimaryBotType = BotType.Tool,
        PrimaryBotName = "Docker 24.0.7",
        RegistryClientCorroborated = registryClientCorroborated,
        Signals = new Dictionary<string, object>()
    };

    /// <summary>
    ///     Minimal <see cref="IActionPolicyRegistry"/> that records which policy
    ///     names were requested and hands back a no-op continue policy, so a test
    ///     can assert on routing WITHOUT executing the real throttle-tools backoff.
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
