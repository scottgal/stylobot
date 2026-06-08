using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.Policies.Evaluation;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Evaluation;

public class PolicyEvaluatorInstrumentationTests
{
    private static PolicyRule MakeRule(string predicate, PolicyAction action, int priority = 0,
        PolicyMode mode = PolicyMode.Live) =>
        new(
            Id: Guid.NewGuid(),
            Scope: new PolicyScope.Wildcard(),
            Priority: priority,
            Predicate: PredicateParser.Parse(predicate),
            Action: action,
            Mode: mode,
            Notes: "",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

    [Fact]
    public async Task Evaluator_records_per_rule_latency_and_decision()
    {
        var log = new InMemoryPolicyDecisionLog();
        var blockRule = MakeRule("bot.type in (scraper, crawler) and score.bot_probability >= 0.7",
            new PolicyAction.Block());
        var challengeRule = MakeRule("geo.country in (CN, RU)", new PolicyAction.Challenge("turnstile"), priority: 1);
        var allowRule = MakeRule("is_human = true", new PolicyAction.Allow(), priority: 2);

        var evaluator = new RuleEvaluationLoop(log);
        var ctx = new Dictionary<string, object?>
        {
            ["bot.type"] = "scraper",
            ["score.bot_probability"] = 0.92m,
            ["geo.country"] = "US",
            ["is_human"] = false
        };

        var winner = await evaluator.EvaluateAsync(
            new[] { blockRule, challengeRule, allowRule },
            ctx,
            requestFingerprint: "fp-test");

        Assert.NotNull(winner);
        Assert.Equal(blockRule.Id, winner!.Id);

        var written = log.Snapshot();
        Assert.Equal(3, written.Count);
        Assert.All(written, d => Assert.Equal(blockRule.Id, d.WinnerRuleId));
        Assert.Single(written, d => d.Matched && d.RuleId == blockRule.Id);
        Assert.True(written.First(d => d.RuleId == blockRule.Id).EvalLatencyTicks > 0);
        // The two skipped rows have EvalLatencyTicks == 0 (not evaluated)
        Assert.Equal(0, written.First(d => d.RuleId == challengeRule.Id).EvalLatencyTicks);
        Assert.Equal(0, written.First(d => d.RuleId == allowRule.Id).EvalLatencyTicks);
        // Fingerprint is propagated to every row in the trace.
        Assert.All(written, d => Assert.Equal("fp-test", d.RequestFingerprint));
    }

    [Fact]
    public async Task Evaluator_records_no_winner_when_nothing_matches()
    {
        var log = new InMemoryPolicyDecisionLog();
        var blockRule = MakeRule("bot.type in (xenomorph)", new PolicyAction.Block());

        var evaluator = new RuleEvaluationLoop(log);
        var ctx = new Dictionary<string, object?> { ["bot.type"] = "human" };
        var winner = await evaluator.EvaluateAsync(new[] { blockRule }, ctx);

        Assert.Null(winner);
        var written = log.Snapshot();
        Assert.Single(written);
        Assert.Equal(Guid.Empty, written[0].WinnerRuleId);
        Assert.False(written[0].Matched);
    }

    [Fact]
    public async Task Observe_mode_rule_matches_but_is_not_returned_as_enforcement_winner()
    {
        var log = new InMemoryPolicyDecisionLog();
        var observeRule = MakeRule("bot.type = scraper", new PolicyAction.Block(), mode: PolicyMode.Observe);
        var liveRule = MakeRule("geo.country = US", new PolicyAction.Allow(), mode: PolicyMode.Live, priority: 1);

        var evaluator = new RuleEvaluationLoop(log);
        var ctx = new Dictionary<string, object?> { ["bot.type"] = "scraper", ["geo.country"] = "US" };
        var winner = await evaluator.EvaluateAsync(new[] { observeRule, liveRule }, ctx);

        // Observe rule's predicate MATCHED -- it's recorded in the log as matched=true,
        // mode=Observe, but the enforcement winner is the next LIVE rule that matches.
        Assert.NotNull(winner);
        Assert.Equal(liveRule.Id, winner!.Id);
        var written = log.Snapshot();
        Assert.Equal(2, written.Count);
        Assert.True(written.First(d => d.RuleId == observeRule.Id).Matched);
        Assert.Equal(PolicyMode.Observe, written.First(d => d.RuleId == observeRule.Id).Mode);
        // Winner id is back-filled on the Observe row too.
        Assert.Equal(liveRule.Id, written.First(d => d.RuleId == observeRule.Id).WinnerRuleId);
    }

    [Fact]
    public async Task Disabled_rules_are_not_evaluated()
    {
        var log = new InMemoryPolicyDecisionLog();
        var disabledRule = MakeRule("bot.type = scraper", new PolicyAction.Block(), mode: PolicyMode.Disabled);
        var liveRule = MakeRule("bot.type = scraper", new PolicyAction.Challenge("turnstile"), mode: PolicyMode.Live, priority: 1);

        var evaluator = new RuleEvaluationLoop(log);
        var ctx = new Dictionary<string, object?> { ["bot.type"] = "scraper" };
        var winner = await evaluator.EvaluateAsync(new[] { disabledRule, liveRule }, ctx);

        Assert.Equal(liveRule.Id, winner!.Id);
        var written = log.Snapshot();
        // Disabled rule not in the log -- it was skipped entirely.
        Assert.DoesNotContain(written, d => d.RuleId == disabledRule.Id);
    }

    [Fact]
    public async Task Draft_rules_are_not_evaluated()
    {
        var log = new InMemoryPolicyDecisionLog();
        var draftRule = MakeRule("bot.type = scraper", new PolicyAction.Block(), mode: PolicyMode.Draft);
        var liveRule = MakeRule("bot.type = scraper", new PolicyAction.Challenge("turnstile"), mode: PolicyMode.Live, priority: 1);

        var evaluator = new RuleEvaluationLoop(log);
        var ctx = new Dictionary<string, object?> { ["bot.type"] = "scraper" };
        var winner = await evaluator.EvaluateAsync(new[] { draftRule, liveRule }, ctx);

        Assert.Equal(liveRule.Id, winner!.Id);
        var written = log.Snapshot();
        Assert.DoesNotContain(written, d => d.RuleId == draftRule.Id);
    }

    [Fact]
    public async Task Latency_uses_TimeSpan_ticks_so_microseconds_math_is_correct()
    {
        var log = new InMemoryPolicyDecisionLog();
        var slowRule = new PolicyRule(
            Id: Guid.NewGuid(),
            Scope: new PolicyScope.Wildcard(),
            Priority: 0,
            Predicate: new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("bot.type", PredicateOp.Matches, "scraper.*"),  // regex -> measurable cost
            Action: new PolicyAction.Block(),
            Mode: PolicyMode.Live,
            Notes: "",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid());

        var evaluator = new RuleEvaluationLoop(log);
        await evaluator.EvaluateAsync(new[] { slowRule },
            new Dictionary<string, object?> { ["bot.type"] = "scraper-v1" });

        var written = log.Snapshot();
        // TimeSpan ticks: 1 ms = 10_000 ticks. We expect sub-millisecond predicate eval,
        // so ticks should be > 0 and < 1_000_000 (100ms).
        var ticks = written.Single().EvalLatencyTicks;
        Assert.InRange(ticks, 1, 1_000_000);
    }
}
