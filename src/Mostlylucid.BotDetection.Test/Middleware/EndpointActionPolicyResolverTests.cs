using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Middleware;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Pins the per-endpoint action-policy override path. Phase 2C of the
///     action-surface work: the orchestrator picks an action at detection
///     time, but operators want <c>/login</c> stricter than
///     <c>/static/*</c>. The attribute surface (<see cref="BotPolicyAttribute.ActionPolicy"/>
///     and <see cref="BotActionAttribute.PolicyName"/>) was already there;
///     the middleware just wasn't reading it.
///
///     The resolver returns the override name; the middleware threads it
///     into <c>AggregatedEvidence.TriggeredActionPolicyName</c> only when
///     no higher-precedence override (honeypot, API-key trust, internal
///     network) has already set one. Detection still runs in full
///     (CLAUDE.md: detect-then-down, never detect-then-skip).
/// </summary>
public class EndpointActionPolicyResolverTests
{
    [Fact]
    public void NoEndpoint_ReturnsNull()
    {
        var ctx = new DefaultHttpContext();
        Assert.Null(EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void EndpointWithoutAttributes_ReturnsNull()
    {
        var ctx = WithEndpointMetadata();
        Assert.Null(EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void BotPolicyAttribute_WithActionPolicy_ReturnsThatName()
    {
        // The recommended pattern from the BotPolicyAttribute docstring:
        // [BotPolicy("strict", ActionPolicy = "block-hard")] on the action.
        var ctx = WithEndpointMetadata(
            new BotPolicyAttribute("strict") { ActionPolicy = "block-hard" });

        Assert.Equal("block-hard", EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void BotPolicyAttribute_WithoutActionPolicy_ReturnsNull()
    {
        // The detection policy is set but no action override: the middleware
        // should fall through to the BotType-mapped action policy.
        var ctx = WithEndpointMetadata(new BotPolicyAttribute("strict"));

        Assert.Null(EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void BotActionAttribute_ReturnsPolicyName()
    {
        // [BotAction("throttle-stealth")] alone on the action.
        var ctx = WithEndpointMetadata(new BotActionAttribute("throttle-stealth"));

        Assert.Equal("throttle-stealth", EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void BotActionAttribute_TakesPrecedence_OverBotPolicyActionPolicy()
    {
        // When both attributes are present, BotActionAttribute is the more
        // specific intent (it exists only to set the action) and wins.
        var ctx = WithEndpointMetadata(
            new BotPolicyAttribute("strict") { ActionPolicy = "block-hard" },
            new BotActionAttribute("silent-drop"));

        Assert.Equal("silent-drop", EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    [Fact]
    public void EmptyOrWhitespaceActionPolicy_IsIgnored()
    {
        // Defensive: an attribute with ActionPolicy = "" (or null after
        // construction) must not override; otherwise the middleware would
        // try to look up an empty policy name and warn.
        var ctx = WithEndpointMetadata(
            new BotPolicyAttribute("strict") { ActionPolicy = "" });

        Assert.Null(EndpointActionPolicyResolver.ResolveFromEndpoint(ctx));
    }

    private static HttpContext WithEndpointMetadata(params object[] metadata)
    {
        var ctx = new DefaultHttpContext();
        var endpoint = new Microsoft.AspNetCore.Http.Endpoint(
            requestDelegate: _ => Task.CompletedTask,
            metadata: new EndpointMetadataCollection(metadata),
            displayName: "test-endpoint");
        ctx.SetEndpoint(endpoint);
        return ctx;
    }
}
