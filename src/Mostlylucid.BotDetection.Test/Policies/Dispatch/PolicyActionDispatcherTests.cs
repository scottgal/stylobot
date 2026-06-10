using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Resolution;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Scheduling;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Dispatch;

/// <summary>
///     Selection-loop + per-handler invariants for
///     <see cref="PolicyActionDispatcher"/>. Each test stands up a minimal
///     resolver double, a handler set, and (where useful) an in-memory
///     decision log, then asserts the dispatcher's contract.
/// </summary>
public sealed class PolicyActionDispatcherTests
{
    // ---- Helpers -----------------------------------------------------------

    private static HttpContext NewHttpContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Request.Host = new HostString("example.com");
        return ctx;
    }

    private static PolicyRule NewRule(
        PolicyAction action,
        PolicyMode mode = PolicyMode.Live,
        PolicyScope? scope = null,
        RuleTriggerOptions? trigger = null,
        Guid? id = null)
    {
        return new PolicyRule(
            Id: id ?? Guid.NewGuid(),
            Scope: scope ?? PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: new PredicateNode.And(Array.Empty<PredicateNode>()),
            Action: action,
            Mode: mode,
            Notes: "test",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: trigger);
    }

    /// <summary>Minimal IPolicyResolver double returning the rules verbatim.</summary>
    private sealed class StubResolver : IPolicyResolver
    {
        public List<EffectiveRule> Rules { get; } = new();
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(PolicyScope scope, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EffectiveRule>>(Rules);

        public Task<IReadOnlyList<EffectiveRule>> EffectiveWithContextAsync(
            PolicyScope scope,
            IReadOnlyDictionary<string, object?> requestSignals,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<EffectiveRule>>(Rules);
        }
    }

    private static PolicyActionDispatcher BuildDispatcher(
        StubResolver resolver,
        IPolicyDecisionLog? decisionLog = null,
        ITokenBucketStore? bucketStore = null,
        IEnumerable<IPolicyActionHandler>? extraHandlers = null,
        IPolicyDecisionLogQueue? queueOverride = null)
    {
        bucketStore ??= new InMemoryTokenBucketStore();
        var handlers = new List<IPolicyActionHandler>
        {
            new AllowActionHandler(),
            new BlockActionHandler(),
            new ObserveActionHandler(),
            new TagActionHandler(),
            new ChallengeActionHandler(),
            new RateLimitActionHandler(store: null),
            new ThrottleActionHandler(bucketStore)
        };
        if (extraHandlers is not null) handlers.AddRange(extraHandlers);
        IPolicyDecisionLogQueue? queue = queueOverride
            ?? (decisionLog is not null
                ? new SyncDecisionLogQueueForTests(decisionLog)
                : null);
        return new PolicyActionDispatcher(resolver, handlers, queue);
    }

    /// <summary>
    ///     Test-only synchronous adapter: forwards <see cref="IPolicyDecisionLogQueue.Enqueue"/>
    ///     straight to <see cref="IPolicyDecisionLog.AppendAsync"/> so the existing
    ///     "log.Snapshot()" assertions stay observable without standing up a real
    ///     coordinator + ticking the channel. Production paths use
    ///     <see cref="PolicyDecisionLogQueue"/> with its bounded channel + Tick1s
    ///     drainer.
    /// </summary>
    private sealed class SyncDecisionLogQueueForTests : IPolicyDecisionLogQueue
    {
        private readonly IPolicyDecisionLog _log;
        public SyncDecisionLogQueueForTests(IPolicyDecisionLog log) => _log = log;
        public long DroppedCount => 0;
        public void Enqueue(PolicyDecision decision)
        {
            _log.AppendAsync(decision).AsTask().GetAwaiter().GetResult();
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- Selection-loop tests ---------------------------------------------

    [Fact]
    public async Task No_rules_match_returns_fallthrough()
    {
        var resolver = new StubResolver();
        var dispatcher = BuildDispatcher(resolver);

        var result = await dispatcher.DispatchAsync(
            NewHttpContext(),
            PolicyScope.Wildcard(),
            new Dictionary<string, object?>(),
            CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
    }

    [Fact]
    public async Task Live_block_rule_short_circuits_with_403()
    {
        var rule = NewRule(new PolicyAction.Block());
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.ContainsKey(BlockActionHandler.PolicyHeader));
    }

    [Fact]
    public async Task Live_allow_rule_falls_through_and_marks_context()
    {
        var rule = NewRule(new PolicyAction.Allow());
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey));
        Assert.Equal(rule.Id, ctx.Items[PolicyActionDispatcher.AllowMarkerItemKey]);
    }

    [Fact]
    public async Task Live_throttle_with_tokens_falls_through()
    {
        var rule = NewRule(new PolicyAction.Throttle(10));
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver, bucketStore: new InMemoryTokenBucketStore());
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Live_throttle_with_exhausted_bucket_returns_429()
    {
        var rule = NewRule(new PolicyAction.Throttle(1));
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var bucket = new InMemoryTokenBucketStore();
        var dispatcher = BuildDispatcher(resolver, bucketStore: bucket);

        // First request admits and consumes the single token.
        var first = await dispatcher.DispatchAsync(
            NewHttpContext(), PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.FallThrough, first);

        // Second request lands at the same scope and finds the bucket empty.
        var ctx2 = NewHttpContext();
        var second = await dispatcher.DispatchAsync(
            ctx2, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.Handled, second);
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx2.Response.StatusCode);
        Assert.Equal("1", ctx2.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task Observe_rule_logs_and_falls_through_to_next_rule()
    {
        var observeRule = NewRule(new PolicyAction.Block(), mode: PolicyMode.Observe);
        var liveRule = NewRule(new PolicyAction.Allow(), mode: PolicyMode.Live);
        var resolver = new StubResolver();
        // Observe first, Live second -- selection loop must keep walking.
        resolver.Rules.Add(new EffectiveRule(observeRule, observeRule.Scope, IsInherited: false));
        resolver.Rules.Add(new EffectiveRule(liveRule, liveRule.Scope, IsInherited: false));
        var log = new InMemoryPolicyDecisionLog();
        var dispatcher = BuildDispatcher(resolver, decisionLog: log);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        // Allow wins -> fall-through with marker.
        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey));
        // Decision log received TWO rows: one for the observed rule, one for the winner.
        var snapshot = log.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(observeRule.Id, snapshot[0].RuleId);
        Assert.Equal(PolicyMode.Observe, snapshot[0].Mode);
        Assert.Equal(liveRule.Id, snapshot[1].RuleId);
    }

    [Fact]
    public async Task Draft_and_disabled_rules_are_skipped_entirely()
    {
        var draft = NewRule(new PolicyAction.Block(), mode: PolicyMode.Draft);
        var disabled = NewRule(new PolicyAction.Block(), mode: PolicyMode.Disabled);
        var live = NewRule(new PolicyAction.Allow(), mode: PolicyMode.Live);
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(draft, draft.Scope, IsInherited: false));
        resolver.Rules.Add(new EffectiveRule(disabled, disabled.Scope, IsInherited: false));
        resolver.Rules.Add(new EffectiveRule(live, live.Scope, IsInherited: false));
        var log = new InMemoryPolicyDecisionLog();
        var dispatcher = BuildDispatcher(resolver, decisionLog: log);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey));
        // Only the winner gets logged -- Draft / Disabled never reach the log.
        var snapshot = log.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(live.Id, snapshot[0].RuleId);
    }

    [Fact]
    public async Task Armed_rule_wins_over_live_rule()
    {
        // Armed Block beats Live Allow, even though Allow would otherwise win.
        var armedBlock = NewRule(new PolicyAction.Block());
        var liveAllow = NewRule(new PolicyAction.Allow());
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(armedBlock, armedBlock.Scope, IsInherited: false, IsArmed: true));
        resolver.Rules.Add(new EffectiveRule(liveAllow, liveAllow.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Armed_rule_with_ActionWhileArmed_substitutes_action()
    {
        // Authored action is Tag (fall-through, mark context). While armed,
        // ActionWhileArmed=Throttle(1) takes over. The first request admits,
        // the second 429s.
        var trigger = new RuleTriggerOptions(
            SustainFor: TimeSpan.FromSeconds(1),
            RecoverAfter: TimeSpan.FromSeconds(1),
            ActionWhileArmed: new PolicyAction.Throttle(1));
        var rule = NewRule(new PolicyAction.Tag("tagged"), trigger: trigger);
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false, IsArmed: true));
        var bucket = new InMemoryTokenBucketStore();
        var dispatcher = BuildDispatcher(resolver, bucketStore: bucket);

        // First armed dispatch -- Throttle(1) admits the request.
        var first = await dispatcher.DispatchAsync(
            NewHttpContext(), PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.FallThrough, first);

        // Second armed dispatch -- bucket exhausted, expect 429.
        var ctx2 = NewHttpContext();
        var second = await dispatcher.DispatchAsync(
            ctx2, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Equal(PolicyDispatchResult.Handled, second);
        Assert.Equal(StatusCodes.Status429TooManyRequests, ctx2.Response.StatusCode);
        // Tag marker was NEVER written -- ActionWhileArmed replaced Tag.
        Assert.False(ctx2.Items.ContainsKey(PolicyActionDispatcher.TagItemKeyPrefix + "tagged"));
    }

    [Fact]
    public async Task Unknown_action_type_falls_through_gracefully()
    {
        var unknown = new UnknownAction();
        var rule = NewRule(unknown);
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var log = new InMemoryPolicyDecisionLog();
        // Build dispatcher WITHOUT a handler for UnknownAction.
        var dispatcher = BuildDispatcher(resolver, decisionLog: log);

        var ctx = NewHttpContext();
        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        // No status mutated -- the legacy path still gets to write the response.
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.NotEqual(StatusCodes.Status429TooManyRequests, ctx.Response.StatusCode);
        // Decision log still gets a row attributing the fall-through.
        var snapshot = log.Snapshot();
        Assert.Single(snapshot);
    }

    [Fact]
    public async Task Decision_log_receives_one_record_per_dispatch()
    {
        var rule = NewRule(new PolicyAction.Block());
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var log = new InMemoryPolicyDecisionLog();
        var dispatcher = BuildDispatcher(resolver, decisionLog: log);

        await dispatcher.DispatchAsync(
            NewHttpContext(), PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        var snapshot = log.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(rule.Id, snapshot[0].RuleId);
        Assert.Equal(rule.Id, snapshot[0].WinnerRuleId);
        Assert.Equal(PolicyMode.Live, snapshot[0].Mode);
        Assert.IsType<PolicyAction.Block>(snapshot[0].Action);
    }

    [Fact]
    public async Task Decision_enqueues_to_queue_and_drains_on_FlushAsync()
    {
        // Wave 4: the dispatcher writes through IPolicyDecisionLogQueue. The
        // production queue drains on Tick1s; here we use a real queue paired
        // with the NullScheduleCoordinator and drive FlushAsync directly.
        var rule = NewRule(new PolicyAction.Block());
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));

        var log = new InMemoryPolicyDecisionLog();
        var queue = new PolicyDecisionLogQueue(log, NullScheduleCoordinator.Instance);
        var dispatcher = BuildDispatcher(resolver, queueOverride: queue);

        await dispatcher.DispatchAsync(
            NewHttpContext(), PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        // The dispatcher enqueued the decision but has NOT awaited any
        // durable write -- the log should still be empty until the queue
        // drains.
        Assert.Empty(log.Snapshot());

        await queue.FlushAsync();

        var snapshot = log.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(rule.Id, snapshot[0].RuleId);
    }

    [Fact]
    public async Task Tag_action_writes_marker_and_falls_through()
    {
        var rule = NewRule(new PolicyAction.Tag("scraper"));
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.FallThrough, result);
        Assert.True(ctx.Items.ContainsKey(PolicyActionDispatcher.TagItemKeyPrefix + "scraper"));
        Assert.Equal(true, ctx.Items[PolicyActionDispatcher.TagItemKeyPrefix + "scraper"]);
    }

    [Fact]
    public async Task Challenge_action_returns_403_with_challenge_header()
    {
        var rule = NewRule(new PolicyAction.Challenge("turnstile"));
        var resolver = new StubResolver();
        resolver.Rules.Add(new EffectiveRule(rule, rule.Scope, IsInherited: false));
        var dispatcher = BuildDispatcher(resolver);
        var ctx = NewHttpContext();

        var result = await dispatcher.DispatchAsync(
            ctx, PolicyScope.Wildcard(), new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Equal(PolicyDispatchResult.Handled, result);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Equal("required", ctx.Response.Headers[ChallengeActionHandler.ChallengeHeader].ToString());
    }

    /// <summary>
    ///     Synthetic action subtype not registered with any handler. Used by
    ///     <see cref="Unknown_action_type_falls_through_gracefully"/>.
    /// </summary>
    private sealed record UnknownAction : PolicyAction;
}
