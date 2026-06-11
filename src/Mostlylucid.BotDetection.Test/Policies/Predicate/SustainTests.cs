using Microsoft.Extensions.Time.Testing;
using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Triggers;

namespace Mostlylucid.BotDetection.Test.Policies.Predicate;

/// <summary>
///     T-Expr coverage: Sustain AST round-trip through the parser/formatter,
///     SustainTermAtom transitions, SustainEvaluator atom keying, the
///     stateless-vs-stateful PredicateEvaluator dispatch, and the
///     TriggerRuleClassifier shape upgrades.
/// </summary>
public class SustainTests
{
    // ---------- Parser ----------

    [Fact]
    public void Parser_recognises_for_after_term()
    {
        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s");

        var sustain = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain>(ast);
        Assert.Equal(TimeSpan.FromSeconds(60), sustain.Duration);

        var inner = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term>(sustain.Inner);
        Assert.Equal("meter.cpu", inner.Facet);
        Assert.Equal(PredicateOp.Gt, inner.Op);
    }

    [Fact]
    public void Parser_for_is_case_insensitive()
    {
        var lower = PredicateParser.Parse("meter.cpu > 80 for 30s");
        var upper = PredicateParser.Parse("meter.cpu > 80 FOR 30s");
        var mixed = PredicateParser.Parse("meter.cpu > 80 For 30s");

        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void Parser_composite_predicate_with_sustain()
    {
        // (meter.cpu > 80 FOR 60s) AND bot.type = "Scraper"
        // The parser binds FOR tighter than AND, so the sustain wraps only
        // the meter term; the AND combines the Sustain with the bot.type term.
        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s and bot.type = \"Scraper\"");

        var and = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.And>(ast);
        Assert.Equal(2, and.Children.Length);

        var sustain = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain>(and.Children[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), sustain.Duration);

        var bot = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term>(and.Children[1]);
        Assert.Equal("bot.type", bot.Facet);
        Assert.Equal("Scraper", bot.Value);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 5 * 60)]
    [InlineData("1h", 60 * 60)]
    public void Parser_accepts_single_unit_durations(string literal, int expectedSeconds)
    {
        var ast = PredicateParser.Parse($"meter.cpu > 80 for {literal}");
        var sustain = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain>(ast);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), sustain.Duration);
    }

    [Fact]
    public void Parser_accepts_composite_duration_1h30m()
    {
        var ast = PredicateParser.Parse("meter.cpu > 80 for 1h30m");
        var sustain = Assert.IsType<Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain>(ast);
        Assert.Equal(TimeSpan.FromMinutes(90), sustain.Duration);
    }

    [Theory]
    [InlineData("meter.cpu > 80 for", "invalid duration literal")]
    [InlineData("meter.cpu > 80 for nope", "invalid duration literal")]
    [InlineData("meter.cpu > 80 for 0s", "invalid duration literal")]
    [InlineData("meter.cpu > 80 for 30", "invalid duration literal")]
    public void Parser_rejects_bad_duration(string expr, string substring)
    {
        var ex = Assert.Throws<PredicateParseException>(() => PredicateParser.Parse(expr));
        Assert.Contains(substring, ex.Message);
    }

    [Theory]
    // The formatter emits canonical normalised durations -- it always picks
    // the largest whole unit that fits. "60s" -> "1m" (same TimeSpan, prettier
    // shorthand). The round-trip is AST-exact, the text may normalise.
    [InlineData("meter.cpu > 80 for 60s",  "meter.cpu > 80 for 1m")]
    [InlineData("meter.cpu > 80 for 30s",  "meter.cpu > 80 for 30s")]
    [InlineData("meter.cpu > 80 for 5m",   "meter.cpu > 80 for 5m")]
    [InlineData("meter.cpu > 80 for 1h",   "meter.cpu > 80 for 1h")]
    [InlineData("meter.cpu > 80 for 1d",   "meter.cpu > 80 for 1d")]
    [InlineData("meter.cpu > 80 for 1h30m","meter.cpu > 80 for 90m")]
    public void Parser_formatter_round_trip(string input, string expectedCanonical)
    {
        var ast = PredicateParser.Parse(input);
        var formatted = PredicateFormatter.Format(ast);
        Assert.Equal(expectedCanonical, formatted);

        // Re-parsing the formatted form yields the same AST -- this is the
        // load-bearing invariant for storage round-trips.
        var reparsed = PredicateParser.Parse(formatted);
        Assert.Equal(ast, reparsed);
    }

    [Fact]
    public void Parser_formatter_round_trip_composite()
    {
        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s and bot.type = scraper");
        var formatted = PredicateFormatter.Format(ast);
        Assert.Equal("meter.cpu > 80 for 1m and bot.type = scraper", formatted);

        var reparsed = PredicateParser.Parse(formatted);
        Assert.Equal(ast, reparsed);
    }

    // ---------- Atom ----------

    [Fact]
    public void Atom_not_satisfied_at_t0_satisfied_at_duration()
    {
        var atom = new SustainTermAtom();
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromSeconds(60);

        Assert.False(atom.IsSatisfied(true, t0, duration));               // first true: arms the timer
        Assert.False(atom.IsSatisfied(true, t0.AddSeconds(30), duration)); // not yet
        Assert.True(atom.IsSatisfied(true, t0.AddSeconds(60), duration));  // sustained
        Assert.True(atom.IsSatisfied(true, t0.AddSeconds(90), duration));  // still sustained
    }

    [Fact]
    public void Atom_resets_when_inner_goes_false()
    {
        var atom = new SustainTermAtom();
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromSeconds(60);

        Assert.False(atom.IsSatisfied(true, t0, duration));
        Assert.True(atom.IsSatisfied(true, t0.AddSeconds(60), duration));
        Assert.False(atom.IsSatisfied(false, t0.AddSeconds(61), duration));
        Assert.Null(atom.FirstObservedTrueAt);
    }

    [Fact]
    public void Atom_rearms_after_reset()
    {
        var atom = new SustainTermAtom();
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var duration = TimeSpan.FromSeconds(60);

        // First arm cycle.
        atom.IsSatisfied(true, t0, duration);
        Assert.True(atom.IsSatisfied(true, t0.AddSeconds(60), duration));

        // Reset.
        atom.IsSatisfied(false, t0.AddSeconds(70), duration);
        Assert.Null(atom.FirstObservedTrueAt);

        // Second arm cycle from a fresh start.
        Assert.False(atom.IsSatisfied(true, t0.AddSeconds(80), duration));
        Assert.False(atom.IsSatisfied(true, t0.AddSeconds(110), duration));
        Assert.True(atom.IsSatisfied(true, t0.AddSeconds(140), duration));
    }

    // ---------- SustainEvaluator atom keying ----------

    [Fact]
    public void Evaluator_shares_atom_for_same_rule_and_inner()
    {
        var evaluator = new SustainEvaluator(TimeProvider.System);
        var ruleId = Guid.NewGuid();
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(30));
        var node2 = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(30));
        var signals = new Dictionary<string, object?> { ["meter.cpu"] = 90m };

        evaluator.IsSatisfied(ruleId, node, signals);
        evaluator.IsSatisfied(ruleId, node2, signals);

        Assert.Equal(1, evaluator.AtomCount);
    }

    [Fact]
    public void Evaluator_isolates_atoms_per_rule()
    {
        var evaluator = new SustainEvaluator(TimeProvider.System);
        var ruleA = Guid.NewGuid();
        var ruleB = Guid.NewGuid();
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(30));
        var signals = new Dictionary<string, object?> { ["meter.cpu"] = 90m };

        evaluator.IsSatisfied(ruleA, node, signals);
        evaluator.IsSatisfied(ruleB, node, signals);

        Assert.Equal(2, evaluator.AtomCount);
    }

    [Fact]
    public void Evaluator_with_fake_time_provider_drives_transitions()
    {
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t0);
        var evaluator = new SustainEvaluator(fake);
        var ruleId = Guid.NewGuid();
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(60));
        var signals = new Dictionary<string, object?> { ["meter.cpu"] = 90m };

        Assert.False(evaluator.IsSatisfied(ruleId, node, signals));
        fake.Advance(TimeSpan.FromSeconds(30));
        Assert.False(evaluator.IsSatisfied(ruleId, node, signals));
        fake.Advance(TimeSpan.FromSeconds(30));
        Assert.True(evaluator.IsSatisfied(ruleId, node, signals));
    }

    [Fact]
    public void Evaluator_resets_when_inner_falsifies()
    {
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t0);
        var evaluator = new SustainEvaluator(fake);
        var ruleId = Guid.NewGuid();
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(60));

        var hot = new Dictionary<string, object?> { ["meter.cpu"] = 90m };
        var cold = new Dictionary<string, object?> { ["meter.cpu"] = 20m };

        Assert.False(evaluator.IsSatisfied(ruleId, node, hot));
        fake.Advance(TimeSpan.FromSeconds(60));
        Assert.True(evaluator.IsSatisfied(ruleId, node, hot));

        // One cold reading drops it, even though the hot was sustained.
        Assert.False(evaluator.IsSatisfied(ruleId, node, cold));

        // Re-arm cycle starts from scratch.
        Assert.False(evaluator.IsSatisfied(ruleId, node, hot));
        fake.Advance(TimeSpan.FromSeconds(60));
        Assert.True(evaluator.IsSatisfied(ruleId, node, hot));
    }

    // ---------- PredicateEvaluator stateless / stateful ----------

    [Fact]
    public void EvaluateStateless_returns_false_for_any_sustain_node()
    {
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(60));
        var signals = new Dictionary<string, object?> { ["meter.cpu"] = 90m };

        Assert.False(PredicateEvaluator.EvaluateStateless(node, signals));
    }

    [Fact]
    public void EvaluateStateless_back_compat_legacy_evaluate_returns_false_for_sustain()
    {
        // The pre-T-Expr two-arg Evaluate signature kept for callers without a
        // sustain context. It must degrade safely to "false" rather than throw.
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var node = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Sustain(inner, TimeSpan.FromSeconds(60));
        var signals = new Dictionary<string, object?> { ["meter.cpu"] = 90m };

        Assert.False(PredicateEvaluator.Evaluate(node, signals));
    }

    [Fact]
    public void Stateful_evaluate_dispatches_through_sustain_evaluator()
    {
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t0);
        var evaluator = new SustainEvaluator(fake);
        var ruleId = Guid.NewGuid();

        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s and bot.type = \"Scraper\"");

        var signals = new Dictionary<string, object?>
        {
            ["meter.cpu"] = 90m,
            ["bot.type"] = "Scraper"
        };

        // Sustain not yet met -> composite predicate is false.
        Assert.False(PredicateEvaluator.Evaluate(ast, signals, ruleId, evaluator));
        fake.Advance(TimeSpan.FromSeconds(60));
        // Sustained -> composite is true.
        Assert.True(PredicateEvaluator.Evaluate(ast, signals, ruleId, evaluator));

        // Flip the per-request bot.type -> AND falls back to false even while
        // Sustain is still satisfied.
        signals["bot.type"] = "Human";
        Assert.False(PredicateEvaluator.Evaluate(ast, signals, ruleId, evaluator));
    }

    [Fact]
    public void Stateful_evaluate_sustain_inside_or_returns_true_when_either_branch_wins()
    {
        var t0 = new DateTimeOffset(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(t0);
        var evaluator = new SustainEvaluator(fake);
        var ruleId = Guid.NewGuid();

        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s or bot.type = \"Scraper\"");

        // Sustain not yet met, bot.type matches -> Or is true via the per-request branch.
        var sigA = new Dictionary<string, object?> { ["meter.cpu"] = 90m, ["bot.type"] = "Scraper" };
        Assert.True(PredicateEvaluator.Evaluate(ast, sigA, ruleId, evaluator));

        // Now drop the per-request branch -- Or relies on the Sustain branch.
        var sigB = new Dictionary<string, object?> { ["meter.cpu"] = 90m, ["bot.type"] = "Human" };
        Assert.False(PredicateEvaluator.Evaluate(ast, sigB, ruleId, evaluator));
        fake.Advance(TimeSpan.FromSeconds(60));
        Assert.True(PredicateEvaluator.Evaluate(ast, sigB, ruleId, evaluator));
    }

    // ---------- TriggerRuleClassifier ----------

    [Fact]
    public void Classifier_meter_only_sustain_qualifies_as_trigger_rule()
    {
        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s");
        var rule = MakeRule(ast, trigger: null);
        Assert.True(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Classifier_sustain_over_per_request_facet_is_not_trigger_rule()
    {
        var ast = PredicateParser.Parse("bot.type = \"Scraper\" for 60s");
        var rule = MakeRule(ast, trigger: null);
        Assert.False(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Classifier_composite_sustain_and_per_request_is_trigger_rule()
    {
        // Sustain inner is meter-only (meter.cpu > 80); the surrounding AND
        // mixes a per-request term but the composite still qualifies because
        // the sustain branch alone needs the trigger-tick path.
        var ast = PredicateParser.Parse("meter.cpu > 80 for 60s and bot.type = \"Scraper\"");
        var rule = MakeRule(ast, trigger: null);
        Assert.True(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Classifier_phase_g_back_compat_is_still_trigger_rule()
    {
        var inner = new Mostlylucid.BotDetection.Policies.Predicate.Predicate.Term("meter.cpu", PredicateOp.Gt, 80m);
        var trigger = new RuleTriggerOptions(SustainFor: TimeSpan.FromSeconds(60), RecoverAfter: TimeSpan.FromSeconds(30));
        var rule = MakeRule(inner, trigger);
        Assert.True(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    [Fact]
    public void Classifier_per_request_only_predicate_is_not_trigger_rule()
    {
        var ast = PredicateParser.Parse("bot.type = \"Scraper\"");
        var rule = MakeRule(ast, trigger: null);
        Assert.False(TriggerRuleClassifier.IsTriggerRule(rule));
    }

    private static PolicyRule MakeRule(
        Mostlylucid.BotDetection.Policies.Predicate.Predicate predicate,
        RuleTriggerOptions? trigger)
        => new(
            Id: Guid.NewGuid(),
            Scope: PolicyScope.Wildcard(),
            Priority: 100,
            Predicate: predicate,
            Action: new PolicyAction.Observe(),
            Mode: PolicyMode.Live,
            Notes: string.Empty,
            Source: "test",
            CreatedAt: DateTimeOffset.UtcNow,
            RevisionId: Guid.NewGuid(),
            AutoPromoteAt: null,
            Trigger: trigger);
}
