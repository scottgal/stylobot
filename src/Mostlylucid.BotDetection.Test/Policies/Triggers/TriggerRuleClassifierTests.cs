using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;
using PredicateNode = Mostlylucid.BotDetection.Policies.Predicate.Predicate;

namespace Mostlylucid.BotDetection.Test.Policies.Triggers;

/// <summary>
///     Coverage for <see cref="TriggerRuleClassifier"/> -- the G3 classifier
///     that decides which rules belong to the oscilloscope MeterTriggerService.
///     A rule is a trigger rule when it has a non-null
///     <see cref="PolicyRule.Trigger"/> AND its predicate references only
///     <c>meter.*</c> facets.
/// </summary>
public sealed class TriggerRuleClassifierTests
{
    private static PredicateNode.Term Meter(string facet, decimal v) =>
        new(facet, PredicateOp.Gt, v);

    private static PredicateNode.Term NonMeter(string facet, string v) =>
        new(facet, PredicateOp.Eq, v);

    private static PolicyRule Rule(PredicateNode predicate, RuleTriggerOptions? trigger)
    {
        var id = Guid.NewGuid();
        return new PolicyRule(
            id,
            PolicyScope.Wildcard(),
            Priority: 100,
            predicate,
            new PolicyAction.Block(),
            PolicyMode.Live,
            Notes: "test",
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: trigger);
    }

    [Fact]
    public void Single_meter_term_with_trigger_is_a_trigger_rule()
    {
        var rule = Rule(Meter("meter.system.cpu_load.current", 80m), new RuleTriggerOptions());
        Assert.True(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Single_meter_term_without_trigger_is_NOT_a_trigger_rule()
    {
        var rule = Rule(Meter("meter.system.cpu_load.current", 80m), trigger: null);
        Assert.False(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Mixed_meter_and_per_request_terms_is_NOT_a_trigger_rule()
    {
        var predicate = new PredicateNode.And(new PredicateNode[]
        {
            Meter("meter.system.cpu_load.current", 80m),
            NonMeter("bot.type", "scraper")
        });
        var rule = Rule(predicate, new RuleTriggerOptions());
        Assert.False(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Nested_meter_only_AND_OR_is_a_trigger_rule()
    {
        var predicate = new PredicateNode.And(new PredicateNode[]
        {
            Meter("meter.alpha.current", 1m),
            new PredicateNode.Or(new PredicateNode[]
            {
                Meter("meter.beta.delta_1m", 100m),
                Meter("meter.gamma.p99", 500m)
            })
        });
        var rule = Rule(predicate, new RuleTriggerOptions());
        Assert.True(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Nested_OR_with_one_non_meter_leaf_is_NOT_a_trigger_rule()
    {
        var predicate = new PredicateNode.Or(new PredicateNode[]
        {
            Meter("meter.alpha.current", 1m),
            NonMeter("score.bot_probability", "0.9")
        });
        var rule = Rule(predicate, new RuleTriggerOptions());
        Assert.False(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Facet_prefix_match_is_case_insensitive()
    {
        // Defensive: the prefix should be matched case-insensitively so the
        // YAML loader's casing decisions never silently kick a rule out of
        // trigger mode.
        Assert.True(TriggerRuleClassifier.IsMeterOnly(
            new PredicateNode.Term("METER.X.current", PredicateOp.Gt, 0m)));
    }

    [Fact]
    public void Empty_AND_is_meter_only_vacuously()
    {
        // Matches PredicateEvaluator's identity-element semantics: an empty
        // AND is vacuously true, so it's vacuously "meter-only" too.
        Assert.True(TriggerRuleClassifier.IsMeterOnly(
            new PredicateNode.And(Array.Empty<PredicateNode>())));
    }
}
