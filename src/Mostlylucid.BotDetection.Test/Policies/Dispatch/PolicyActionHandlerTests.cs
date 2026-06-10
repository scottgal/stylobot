using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.RateLimit;
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
        var bucket = new InMemoryTokenBucketStore();
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

    /// <summary>
    ///     Wave 3 regression: RateLimit and Throttle share ONE
    ///     <see cref="ITokenBucketStore"/> primitive. A tracking store records
    ///     every TryConsume call; dispatching one of each action MUST land both
    ///     calls in the same store instance with disjoint policy-name prefixes
    ///     (no key collisions across the two action types).
    /// </summary>
    [Fact]
    public async Task Throttle_and_RateLimit_use_same_bucket_store_instance()
    {
        var trackingStore = new RecordingBucketStore();
        var throttleHandler = new ThrottleActionHandler(trackingStore);
        var rateLimitHandler = new RateLimitActionHandler(trackingStore);

        var throttleRule = NewRule(new PolicyAction.Throttle(10));
        var rateLimitRule = NewRule(new PolicyAction.RateLimit(60));

        await throttleHandler.HandleAsync(
            NewHttpContext(), throttleRule, throttleRule.Action, CancellationToken.None);
        await rateLimitHandler.HandleAsync(
            NewHttpContext(), rateLimitRule, rateLimitRule.Action, CancellationToken.None);

        // Both calls hit the SAME ITokenBucketStore instance -- the test wouldn't
        // see two calls otherwise.
        Assert.Equal(2, trackingStore.Calls.Count);

        // Namespace prefixes diverge so the two action types can never collide
        // on the same underlying bucket key, even if their other inputs match.
        Assert.Contains(trackingStore.Calls,
            c => c.PolicyName.StartsWith(ThrottleActionHandler.BucketPolicyPrefix, StringComparison.Ordinal));
        Assert.Contains(trackingStore.Calls,
            c => c.PolicyName.StartsWith(RateLimitActionHandler.BucketPolicyPrefix, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Test-only <see cref="ITokenBucketStore"/> that records every
    ///     <see cref="ITokenBucketStore.TryConsume"/> call. Always admits --
    ///     this fixture is about proving the same instance was used, not
    ///     about bucket behaviour (the in-memory store has its own test
    ///     coverage in <c>InMemoryTokenBucketStoreTests</c>).
    /// </summary>
    private sealed class RecordingBucketStore : ITokenBucketStore
    {
        public List<(string PolicyName, string Key, int Capacity, int RefillRpm)> Calls { get; } = new();

        public bool TryConsume(string policyName, string key, int capacity, int refillRatePerMinute)
        {
            Calls.Add((policyName, key, capacity, refillRatePerMinute));
            return true;
        }

        public BucketSnapshot? Peek(string policyName, string key) => null;
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
