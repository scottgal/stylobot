using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Telemetry;
using Mostlylucid.BotDetection.RateLimit;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

// C15b -- the FOSS PolicyActionDispatcher must call IPolicyHitRecorder.Record
// for every rule that fires, otherwise the dashboard posture card reads from
// an always-empty atom and renders Permissive/0/0/0/0/0 regardless of real
// traffic. The recorder write happens AFTER the winning rule is selected and
// uses the rule's SourceScope canonical key so the view component (which is
// invoked at the same scope key) sees the bucket.
public sealed class PolicyDispatchHitRecordingTests
{
    private sealed class RecordingPolicyHitRecorder : IPolicyHitRecorder
    {
        public List<(string ScopeKey, PolicyIntentKind Intent)> Calls { get; } = new();

        public void Record(string scopeKey, PolicyIntentKind intent)
            => Calls.Add((scopeKey, intent));
    }

    private sealed class StubResolver : IPolicyResolver
    {
        public List<EffectiveRule> Rules { get; } = new();

        public Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EffectiveRule>>(Rules);

        public Task<IReadOnlyList<EffectiveRule>> EffectiveWithContextAsync(
            PolicyScope scope,
            IReadOnlyDictionary<string, object?> requestSignals,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EffectiveRule>>(Rules);
    }

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Request.Host = new HostString("example.com");
        return ctx;
    }

    private static PolicyRule NewRule(PolicyAction action, PolicyScope scope, PolicyMode mode = PolicyMode.Live)
        => new(
            Id: Guid.NewGuid(),
            Scope: scope,
            Priority: 100,
            Predicate: new PredicateNode.And(Array.Empty<PredicateNode>()),
            Action: action,
            Mode: mode,
            Notes: "test",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: null);

    private static PolicyActionDispatcher BuildDispatcher(
        StubResolver resolver,
        IPolicyHitRecorder recorder)
    {
        var handlers = new List<IPolicyActionHandler>
        {
            new AllowActionHandler(),
            new BlockActionHandler(),
            new ObserveActionHandler(),
            new TagActionHandler(),
            new ChallengeActionHandler(),
            new RateLimitActionHandler(store: null),
            new ThrottleActionHandler(new InMemoryTokenBucketStore()),
        };
        return new PolicyActionDispatcher(
            resolver: resolver,
            handlers: handlers,
            decisionLogQueue: null,
            timeProvider: null,
            logger: null,
            hitRecorder: recorder,
            intentClassifier: new PolicyIntentClassifier());
    }

    [Fact]
    public async Task Live_block_rule_records_block_intent_under_source_scope_key()
    {
        // A Block rule attached to a Domain scope fires; the recorder must
        // see (Domain.ToScopeKey(), PolicyIntentKind.Block) so a dashboard
        // viewing the same Domain page sees the bucket populated.
        var scope = PolicyScope.Domain("example.com");
        var rule = NewRule(new PolicyAction.Block(), scope);
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, scope, IsInherited: false));

        var recorder = new RecordingPolicyHitRecorder();
        var dispatcher = BuildDispatcher(resolver, recorder);

        var result = await dispatcher.DispatchAsync(
            NewHttpContext(),
            scope,
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        var (scopeKey, intent) = Assert.Single(recorder.Calls);
        Assert.Equal("domain|example.com|||", scopeKey);
        Assert.Equal(PolicyIntentKind.Block, intent);
    }
}
