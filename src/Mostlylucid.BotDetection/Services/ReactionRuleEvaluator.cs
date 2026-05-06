using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionRuleEvaluator(ISignalGroupRegistry groupRegistry)
{
    public (bool Satisfied, string TriggerBy, double SignalValue) Evaluate(
        ReactionConditionSet conditionSet,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string trackingPrefix)
    {
        if (conditionSet.Rules.Count == 0)
            return (false, "", 0);

        string triggerBy = "";
        double triggerValue = 0;

        var results = conditionSet.Rules
            .Select((rule, i) =>
            {
                var (ok, sig, val) = EvaluateRule(rule, signals, tracker, $"{trackingPrefix}:{i}");
                if (ok && triggerBy.Length == 0) { triggerBy = sig; triggerValue = val; }
                return ok;
            })
            .ToList();

        var satisfied = conditionSet.IsAll ? results.All(r => r) : results.Any(r => r);
        return (satisfied, triggerBy, triggerValue);
    }

    private (bool Ok, string Signal, double Value) EvaluateRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        if (!string.IsNullOrEmpty(rule.SignalGroup))
            return EvaluateGroupRule(rule, signals, tracker, ruleKey);

        if (string.IsNullOrEmpty(rule.Signal))
            return (false, "", 0);

        var (conditionMet, value) = EvaluateThreshold(rule, signals, rule.Signal);
        var satisfied = tracker.IsSatisfied(ruleKey, conditionMet, rule.ForSeconds);
        return (satisfied, rule.Signal, value);
    }

    private (bool Ok, string Signal, double Value) EvaluateGroupRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        var groupSignals = groupRegistry.Resolve(rule.SignalGroup!);
        if (groupSignals.Count == 0)
            return (false, "", 0);

        string firstTriggerSig = "";
        double firstTriggerVal = 0;

        var results = groupSignals.Select((sig, i) =>
        {
            var (conditionMet, val) = EvaluateThreshold(rule, signals, sig);
            var satisfied = tracker.IsSatisfied($"{ruleKey}:grp{i}", conditionMet, rule.ForSeconds);
            if (satisfied && firstTriggerSig.Length == 0) { firstTriggerSig = sig; firstTriggerVal = val; }
            return satisfied;
        }).ToList();

        var ok = string.Equals(rule.GroupCondition, "all", StringComparison.OrdinalIgnoreCase)
            ? results.All(r => r)
            : results.Any(r => r);
        return (ok, firstTriggerSig, firstTriggerVal);
    }

    private static (bool Met, double Value) EvaluateThreshold(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        string signalKey)
    {
        if (!signals.TryGetValue(signalKey, out var value))
            return (false, 0);

        if (rule.Above.HasValue && rule.Below.HasValue)
            return (value > rule.Above.Value && value < rule.Below.Value, value);
        if (rule.Above.HasValue)
            return (value > rule.Above.Value, value);
        if (rule.Below.HasValue)
            return (value < rule.Below.Value, value);
        return (false, 0);
    }
}
