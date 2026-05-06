using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

public sealed class ReactionRuleEvaluator(ISignalGroupRegistry groupRegistry)
{
    public bool Evaluate(
        ReactionConditionSet conditionSet,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string trackingPrefix)
    {
        var results = conditionSet.Rules
            .Select((rule, i) => EvaluateRule(rule, signals, tracker, $"{trackingPrefix}:{i}"))
            .ToList();

        return conditionSet.IsAll ? results.All(r => r) : results.Any(r => r);
    }

    private bool EvaluateRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        if (!string.IsNullOrEmpty(rule.SignalGroup))
            return EvaluateGroupRule(rule, signals, tracker, ruleKey);

        if (string.IsNullOrEmpty(rule.Signal))
            return false;

        var conditionMet = EvaluateThreshold(rule, signals, rule.Signal);
        return tracker.IsSatisfied(ruleKey, conditionMet, rule.ForSeconds);
    }

    private bool EvaluateGroupRule(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        HysteresisTracker tracker,
        string ruleKey)
    {
        var groupSignals = groupRegistry.Resolve(rule.SignalGroup!);
        if (groupSignals.Count == 0)
            return false;

        var results = groupSignals.Select((sig, i) =>
        {
            var conditionMet = EvaluateThreshold(rule, signals, sig);
            return tracker.IsSatisfied($"{ruleKey}:grp{i}", conditionMet, rule.ForSeconds);
        }).ToList();

        return string.Equals(rule.GroupCondition, "all", StringComparison.OrdinalIgnoreCase)
            ? results.All(r => r)
            : results.Any(r => r);
    }

    private static bool EvaluateThreshold(
        ReactionRule rule,
        IReadOnlyDictionary<string, double> signals,
        string signalKey)
    {
        if (!signals.TryGetValue(signalKey, out var value))
            return false;

        if (rule.Above.HasValue && rule.Below.HasValue)
            return value > rule.Above.Value && value < rule.Below.Value;
        if (rule.Above.HasValue)
            return value > rule.Above.Value;
        if (rule.Below.HasValue)
            return value < rule.Below.Value;
        return false;
    }
}
