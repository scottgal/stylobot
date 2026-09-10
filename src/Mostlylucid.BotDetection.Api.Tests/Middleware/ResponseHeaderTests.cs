using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Api.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Api.Tests.Middleware;

/// <summary>
///     The <c>X-StyloBot-*</c> response headers are the header-mode contract every
///     SDK client reads (sdk/node, sdk/caddy, sdk/go). Two of the values must agree
///     with the rest of the system instead of being re-derived here:
///     <list type="bullet">
///         <item>
///             <c>X-StyloBot-IsBot</c> is the canonical classifier cut,
///             <c>bot_probability &gt;= Classification.BotFloor</c> -- the SAME cut
///             <c>HttpContext.IsBot()</c>, the dashboard and the stored <c>is_bot</c>
///             use. A hardcoded 0.70 silently disagrees with any host that
///             configures a different floor, which is exactly the
///             "dashboard disagrees with the score" failure the single-classifier
///             rule exists to prevent.
///         </item>
///         <item>
///             <c>X-StyloBot-Action</c> is the action policy the pipeline actually
///             RESOLVED for this request
///             (<see cref="AggregatedEvidence.TriggeredActionPolicyName"/>), mapped
///             to the documented Allow/Throttle/Challenge/Block vocabulary. Deriving
///             it a second time from <see cref="RiskBand"/> advertises an action the
///             host may never take: observe-only posture resolves
///             <c>throttle-stealth</c>, while the risk-band switch said Block.
///         </item>
///     </list>
/// </summary>
public class ResponseHeaderTests
{
    /// <summary>Single source of truth for the default floor is ClassificationOptions itself.</summary>
    private static double DefaultBotFloor => new ClassificationOptions().BotFloor;

    private static AggregatedEvidence Evidence(
        double botProbability = 0.92,
        RiskBand riskBand = RiskBand.High,
        string? triggeredActionPolicyName = null) => new()
    {
        BotProbability = botProbability,
        Confidence = 0.87,
        RiskBand = riskBand,
        PrimaryBotType = BotType.Scraper,
        PrimaryBotName = "GPTBot",
        ThreatScore = 0.15,
        ThreatBand = ThreatBand.Low,
        TriggeredActionPolicyName = triggeredActionPolicyName,
        TotalProcessingTimeMs = 4,
        ContributingDetectors = new HashSet<string>(),
        Signals = new Dictionary<string, object>()
    };

    private static DefaultHttpContext NewContext(
        AggregatedEvidence evidence,
        double? botFloor = null,
        params (string Name, ActionType Type)[] resolvedPolicies)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<BotDetectionOptions>>(Options.Create(new BotDetectionOptions
        {
            Classification = new ClassificationOptions { BotFloor = botFloor ?? DefaultBotFloor }
        }));

        var registry = new Mock<IActionPolicyRegistry>();
        foreach (var (name, type) in resolvedPolicies)
            registry.Setup(r => r.GetPolicy(name)).Returns(new StubActionPolicy(name, type));
        services.AddSingleton(registry.Object);

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Items["BotDetection.AggregatedEvidence"] = evidence;
        return context;
    }

    [Fact]
    public void InjectHeaders_WritesAllExpectedHeaders()
    {
        var context = NewContext(
            Evidence(triggeredActionPolicyName: "block-hard"),
            resolvedPolicies: ("block-hard", ActionType.Block));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("true", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("0.92", context.Response.Headers["X-StyloBot-Probability"].ToString());
        Assert.Equal("0.87", context.Response.Headers["X-StyloBot-Confidence"].ToString());
        Assert.Equal("Scraper", context.Response.Headers["X-StyloBot-BotType"].ToString());
        Assert.Equal("GPTBot", context.Response.Headers["X-StyloBot-BotName"].ToString());
        Assert.Equal("High", context.Response.Headers["X-StyloBot-RiskBand"].ToString());
        Assert.Equal("Block", context.Response.Headers["X-StyloBot-Action"].ToString());
        Assert.Equal("0.15", context.Response.Headers["X-StyloBot-ThreatScore"].ToString());
        Assert.Equal("Low", context.Response.Headers["X-StyloBot-ThreatBand"].ToString());
    }

    [Fact]
    public void InjectHeaders_NoEvidence_NoHeaders()
    {
        var context = new DefaultHttpContext();
        ResponseHeaderInjection.InjectHeaders(context);
        Assert.False(context.Response.Headers.ContainsKey("X-StyloBot-IsBot"));
    }

    [Fact]
    public void InjectHeaders_HumanVerdict_IsBotFalse()
    {
        var context = NewContext(Evidence(botProbability: 0.12, riskBand: RiskBand.VeryLow));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("false", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    /// <summary>
    ///     The is_bot cut is Classification.BotFloor, not a private constant. A host
    ///     that raises the floor must not get headers claiming a bot the dashboard
    ///     and <c>HttpContext.IsBot()</c> call human.
    /// </summary>
    [Fact]
    public void InjectHeaders_IsBotUsesConfiguredBotFloor()
    {
        var context = NewContext(Evidence(botProbability: 0.72), botFloor: 0.75);

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("false", context.Response.Headers["X-StyloBot-IsBot"].ToString());
    }

    [Fact]
    public void InjectHeaders_IsBotIsInclusiveAtTheFloor()
    {
        var context = NewContext(Evidence(botProbability: DefaultBotFloor));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("true", context.Response.Headers["X-StyloBot-IsBot"].ToString());
    }

    /// <summary>
    ///     The resolved policy decides the action, not the risk band. Observe-only
    ///     posture resolves throttle-stealth for a high-risk bot; the header must say
    ///     Throttle (what the operator configured) rather than Block (what a
    ///     risk-band switch guesses).
    /// </summary>
    [Fact]
    public void InjectHeaders_ActionComesFromResolvedPolicy_NotRiskBand()
    {
        var context = NewContext(
            Evidence(riskBand: RiskBand.High, triggeredActionPolicyName: "throttle-stealth"),
            resolvedPolicies: ("throttle-stealth", ActionType.Throttle));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("Throttle", context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    [Theory]
    [InlineData(ActionType.Block, "Block")]
    [InlineData(ActionType.Challenge, "Challenge")]
    [InlineData(ActionType.Throttle, "Throttle")]
    [InlineData(ActionType.LogOnly, "Allow")]
    [InlineData(ActionType.Escalate, "Allow")]
    public void InjectHeaders_MapsResolvedPolicyIntent(ActionType actionType, string expected)
    {
        // RiskBand stays High throughout: the old risk-band switch would answer
        // "Block" for every one of these rows.
        var context = NewContext(
            Evidence(riskBand: RiskBand.High, triggeredActionPolicyName: "resolved"),
            resolvedPolicies: ("resolved", actionType));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal(expected, context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    /// <summary>
    ///     No action policy resolved ⇒ nothing acted on this request. Advertising
    ///     Block/VeryHigh from the risk band alone would tell a downstream enforcer
    ///     to block traffic this host chose not to block.
    /// </summary>
    [Fact]
    public void InjectHeaders_NoResolvedPolicy_ActionIsAllow()
    {
        var context = NewContext(Evidence(botProbability: 0.99, riskBand: RiskBand.VeryHigh));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    /// <summary>
    ///     A name that is not in the registry cannot be resolved to an intent. Report
    ///     nothing rather than inventing an action from the risk band.
    /// </summary>
    [Fact]
    public void InjectHeaders_UnknownResolvedPolicyName_DoesNotInventAnAction()
    {
        var context = NewContext(Evidence(triggeredActionPolicyName: "not-registered"));

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    /// <summary>
    ///     Header emission must not depend on the request having a service provider
    ///     (a unit test, a minimal host): the configured floor falls back to the
    ///     ClassificationOptions default and the action to Allow.
    /// </summary>
    [Fact]
    public void InjectHeaders_WithoutRequestServices_StillEmitsTheHeaders()
    {
        var context = new DefaultHttpContext();
        context.Items["BotDetection.AggregatedEvidence"] = Evidence();

        ResponseHeaderInjection.InjectHeaders(context);

        Assert.Equal("true", context.Response.Headers["X-StyloBot-IsBot"].ToString());
        Assert.Equal("Allow", context.Response.Headers["X-StyloBot-Action"].ToString());
    }

    private sealed class StubActionPolicy(string name, ActionType actionType) : IActionPolicy
    {
        public string Name { get; } = name;
        public ActionType ActionType { get; } = actionType;

        public Task<ActionResult> ExecuteAsync(
            HttpContext context, AggregatedEvidence evidence, CancellationToken cancellationToken = default)
            => Task.FromResult(ActionResult.Allowed());
    }
}
