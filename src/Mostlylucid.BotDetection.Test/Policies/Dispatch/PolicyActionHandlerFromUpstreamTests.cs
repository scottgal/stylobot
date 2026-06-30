using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

/// <summary>
///     Pins the closed-loop feedback gate at the dispatch handlers:
///     each handler that writes a stylobot-synthesised status code MUST
///     mark <see cref="StyloBotResponseSignalExtensions.MarkResponseFromStyloBot"/>
///     so detector arms downstream don't read stylobot's own enforcement
///     codes as additional bot evidence on the visitor's next request.
/// </summary>
public sealed class PolicyActionHandlerFromUpstreamTests
{
    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static PolicyRule NewRule(PolicyAction action) => new(
        Id: Guid.NewGuid(),
        Scope: PolicyScope.Wildcard(),
        Priority: 100,
        Predicate: new PredicateNode.And(Array.Empty<PredicateNode>()),
        Action: action,
        Mode: PolicyMode.Live,
        Notes: "test",
        Source: "test",
        CreatedAt: DateTimeOffset.UtcNow,
        RevisionId: Guid.NewGuid());

    [Fact]
    public async Task BlockActionHandler_marks_response_as_from_stylobot()
    {
        var handler = new BlockActionHandler();
        var rule = NewRule(new PolicyAction.Block());
        var ctx = NewHttpContext();

        await handler.HandleAsync(ctx, rule, rule.Action, CancellationToken.None);

        Assert.False(ctx.IsResponseFromUpstream());
    }

    [Fact]
    public async Task ChallengeActionHandler_marks_response_as_from_stylobot()
    {
        var handler = new ChallengeActionHandler();
        var rule = NewRule(new PolicyAction.Challenge("captcha"));
        var ctx = NewHttpContext();

        await handler.HandleAsync(ctx, rule, rule.Action, CancellationToken.None);

        Assert.False(ctx.IsResponseFromUpstream());
    }
}
