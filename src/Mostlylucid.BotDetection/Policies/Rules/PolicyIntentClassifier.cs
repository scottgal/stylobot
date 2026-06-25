using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Policies.Rules;

public sealed class PolicyIntentClassifier
{
    private const string LockdownKey = "org.lockdown";

    public PolicyIntentKind Classify(PolicyRule rule)
    {
        if (rule.Mode == PolicyMode.Observe) return PolicyIntentKind.Observe;
        if (PredicateReferencesLockdown(rule.Predicate)) return PolicyIntentKind.Lockdown;
        return rule.Action.Intent;
    }

    private static bool PredicateReferencesLockdown(Predicate.Predicate predicate) =>
        predicate switch
        {
            Predicate.Predicate.Term term => string.Equals(term.Facet, LockdownKey, StringComparison.Ordinal),
            Predicate.Predicate.And and => and.Children.Any(PredicateReferencesLockdown),
            Predicate.Predicate.Or or => or.Children.Any(PredicateReferencesLockdown),
            Predicate.Predicate.Sustain sustain => PredicateReferencesLockdown(sustain.Inner),
            _ => false,
        };
}