using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

public class ReactionRuleEvaluatorTests
{
    private static SignalGroupRegistry EmptyRegistry() => new SignalGroupRegistry([]);

    private static Dictionary<string, double> Signals(params (string key, double val)[] pairs)
        => pairs.ToDictionary(p => p.key, p => p.val);

    [Fact]
    public void Evaluate_AboveRule_TimerJustStarted_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };

        var (satisfied, _, _) = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.10)), tracker, "test:activate");
        Assert.False(satisfied);
    }

    [Fact]
    public void Evaluate_AboveRule_BelowThreshold_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };
        tracker.ForceFirstTrue("test:activate:0", DateTime.UtcNow.AddSeconds(-70));

        // Signal is below threshold so condition is false; hysteresis irrelevant
        var (satisfied, _, _) = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.01)), tracker, "test:activate");
        Assert.False(satisfied);
    }

    [Fact]
    public void Evaluate_AboveRule_SatisfiedAfterHysteresis_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "any",
            Rules = [new ReactionRule { Signal = "response.error_rate_5xx", Above = 0.05, ForSeconds = 60.0 }]
        };
        tracker.ForceFirstTrue("test:activate:0", DateTime.UtcNow.AddSeconds(-70));

        var (satisfied, _, _) = evaluator.Evaluate(conditionSet, Signals(("response.error_rate_5xx", 0.10)), tracker, "test:activate");
        Assert.True(satisfied);
    }

    [Fact]
    public void Evaluate_AllCondition_OneRuleNotSatisfied_ReturnsFalse()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "all",
            Rules =
            [
                new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 30.0 },
                new ReactionRule { Signal = "response.rate_429", Below = 0.01, ForSeconds = 30.0 }
            ]
        };
        tracker.ForceFirstTrue("test:deactivate:0", DateTime.UtcNow.AddSeconds(-35));
        // rate_429 is still above threshold so second rule not satisfied
        var (r1, _, _) = evaluator.Evaluate(
            conditionSet,
            Signals(("response.error_rate_5xx", 0.01), ("response.rate_429", 0.05)),
            tracker, "test:deactivate");
        Assert.False(r1);
    }

    [Fact]
    public void Evaluate_AllCondition_BothSatisfied_ReturnsTrue()
    {
        var tracker = new HysteresisTracker();
        var evaluator = new ReactionRuleEvaluator(EmptyRegistry());
        var conditionSet = new ReactionConditionSet
        {
            Condition = "all",
            Rules =
            [
                new ReactionRule { Signal = "response.error_rate_5xx", Below = 0.02, ForSeconds = 30.0 },
                new ReactionRule { Signal = "response.rate_429", Below = 0.01, ForSeconds = 30.0 }
            ]
        };
        tracker.ForceFirstTrue("test:deactivate:0", DateTime.UtcNow.AddSeconds(-35));
        tracker.ForceFirstTrue("test:deactivate:1", DateTime.UtcNow.AddSeconds(-35));
        var (r2, _, _) = evaluator.Evaluate(
            conditionSet,
            Signals(("response.error_rate_5xx", 0.01), ("response.rate_429", 0.005)),
            tracker, "test:deactivate");
        Assert.True(r2);
    }
}
