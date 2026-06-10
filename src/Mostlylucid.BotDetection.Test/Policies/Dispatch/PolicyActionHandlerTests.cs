using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Throttle;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

/// <summary>
///     Focused per-handler tests. Each handler gets a known action + synthetic
///     <see cref="HttpContext"/>; the test asserts the response state matches
///     the legacy shape (status + headers + body envelope).
/// </summary>
public sealed class PolicyActionHandlerTests
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

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task AllowActionHandler_marks_context_and_falls_through()
    {
        var handler = new AllowActionHandler();
        var rule = NewRule(new PolicyAction.Allow());
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, rule.Action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey));
        Assert.Equal(rule.Id, ctx.Items[PolicyActionDispatcher.AllowMarkerItemKey]);
    }

    [Fact]
    public async Task BlockActionHandler_writes_403_with_legacy_body_shape()
    {
        var handler = new BlockActionHandler();
        var rule = NewRule(new PolicyAction.Block());
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, rule.Action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        Assert.Equal((int)HttpStatusCode.Forbidden, ctx.Response.StatusCode);
        Assert.StartsWith("application/json", ctx.Response.ContentType);
        Assert.Contains(rule.Id.ToString(),
            ctx.Response.Headers[BlockActionHandler.PolicyHeader].ToString());

        var body = await ReadBodyAsync(ctx);
        // Legacy BlockedResponse field names rendered as camelCase keys.
        Assert.Contains("\"error\"", body);
        Assert.Contains("\"reason\"", body);
        Assert.Contains("\"riskScore\"", body);
        Assert.Contains("\"policy\"", body);
    }

    [Fact]
    public async Task ObserveActionHandler_does_nothing()
    {
        var handler = new ObserveActionHandler();
        var rule = NewRule(new PolicyAction.Observe());
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, rule.Action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.Equal(200, ctx.Response.StatusCode);   // unmutated default
        Assert.Empty(ctx.Items);
    }

    [Fact]
    public async Task TagActionHandler_writes_marker_and_falls_through()
    {
        var handler = new TagActionHandler();
        var action = new PolicyAction.Tag("scraper");
        var rule = NewRule(action);
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.TagItemKeyPrefix + "scraper"));
    }

    [Fact]
    public async Task TagActionHandler_with_blank_name_falls_through_without_writing()
    {
        var handler = new TagActionHandler();
        var action = new PolicyAction.Tag(" ");
        var rule = NewRule(action);
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        // No key with the prefix gets written for a blank name.
        Assert.DoesNotContain(ctx.Items.Keys, k =>
            k is string s && s.StartsWith(PolicyActionDispatcher.TagItemKeyPrefix));
    }

    [Fact]
    public async Task ChallengeActionHandler_writes_403_with_challenge_headers_and_body()
    {
        var handler = new ChallengeActionHandler();
        var action = new PolicyAction.Challenge("turnstile");
        var rule = NewRule(action);
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        Assert.Equal((int)HttpStatusCode.Forbidden, ctx.Response.StatusCode);
        Assert.Equal("required", ctx.Response.Headers[ChallengeActionHandler.ChallengeHeader].ToString());
        Assert.Contains(rule.Id.ToString(),
            ctx.Response.Headers[BlockActionHandler.PolicyHeader].ToString());

        var body = await ReadBodyAsync(ctx);
        Assert.Contains("\"error\"", body);
        Assert.Contains("\"challengeType\"", body);
        Assert.Contains("\"riskScore\"", body);
        Assert.Contains("turnstile", body);
    }

    [Fact]
    public async Task ThrottleActionHandler_admits_first_request_and_429s_when_drained()
    {
        var bucket = new ThrottleBucketRegistry();
        var handler = new ThrottleActionHandler(bucket);
        var action = new PolicyAction.Throttle(1);
        var rule = NewRule(action);

        var first = await handler.HandleAsync(NewHttpContext(), rule, action, CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.FallThrough, first);

        var ctx2 = NewHttpContext();
        var second = await handler.HandleAsync(ctx2, rule, action, CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.Handled, second);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, ctx2.Response.StatusCode);
        Assert.Equal("1", ctx2.Response.Headers["Retry-After"].ToString());
        Assert.Contains(rule.Id.ToString(),
            ctx2.Response.Headers[BlockActionHandler.PolicyHeader].ToString());
    }

    [Fact]
    public async Task RateLimitActionHandler_without_store_falls_through()
    {
        var handler = new RateLimitActionHandler(store: null);
        var action = new PolicyAction.RateLimit(10);
        var rule = NewRule(action);
        var ctx = NewHttpContext();

        var result = await handler.HandleAsync(ctx, rule, action, CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.Equal(200, ctx.Response.StatusCode);   // unmutated
    }

    [Fact]
    public async Task RateLimitActionHandler_with_store_admits_within_capacity_then_429s()
    {
        var store = new Mostlylucid.BotDetection.RateLimit.InMemoryTokenBucketStore();
        var handler = new RateLimitActionHandler(store);
        var action = new PolicyAction.RateLimit(1);
        var rule = NewRule(action);

        // Stash a fake signal bag so the visitor key resolution prefers fingerprint.
        var visitor = "fp-test-1";
        var first = NewHttpContext();
        first.Items[PolicyActionDispatcher.RequestSignalsItemKey] = new Dictionary<string, object?>
        {
            ["signature.primary"] = visitor
        };
        var second = NewHttpContext();
        second.Items[PolicyActionDispatcher.RequestSignalsItemKey] = new Dictionary<string, object?>
        {
            ["signature.primary"] = visitor
        };

        var firstResult = await handler.HandleAsync(first, rule, action, CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.FallThrough, firstResult);

        var secondResult = await handler.HandleAsync(second, rule, action, CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.Handled, secondResult);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, second.Response.StatusCode);
        Assert.Equal("1", second.Response.Headers["Retry-After"].ToString());
    }
}
