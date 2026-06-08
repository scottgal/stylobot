using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     Default <see cref="IPolicyResolver"/> implementation. Walks the scope
///     chain (<see cref="PolicyScope.Endpoint"/> →
///     <see cref="PolicyScope.Subdomain"/> →
///     <see cref="PolicyScope.Domain"/> →
///     <see cref="PolicyScope.Wildcard"/>) and asks the underlying
///     <see cref="IPolicyRuleStore"/> for the concatenated stack. Predicate
///     filtering is delegated to <see cref="PredicateEvaluator"/>.
/// </summary>
public sealed class DefaultPolicyResolver(IPolicyRuleStore store) : IPolicyResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(
        PolicyScope scope,
        CancellationToken ct = default)
    {
        var path = WalkPath(scope);
        var rules = await store.GetEffectiveRulesAsync(path, ct).ConfigureAwait(false);

        var result = new List<EffectiveRule>(rules.Count);
        foreach (var rule in rules)
            result.Add(new EffectiveRule(rule, rule.Scope, IsInherited: !ScopeEquals(rule.Scope, scope)));
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EffectiveRule>> EffectiveWithContextAsync(
        PolicyScope scope,
        IReadOnlyDictionary<string, object?> requestSignals,
        CancellationToken ct = default)
    {
        var all = await EffectiveAsync(scope, ct).ConfigureAwait(false);

        var result = new List<EffectiveRule>(all.Count);
        foreach (var entry in all)
        {
            if (PredicateEvaluator.Evaluate(entry.Rule.Predicate, requestSignals))
                result.Add(entry);
        }
        return result;
    }

    /// <summary>
    ///     Derive the most-specific-first scope path for <paramref name="scope"/>.
    ///     Order: Endpoint → Subdomain → Domain → Wildcard.
    /// </summary>
    private static IReadOnlyList<PolicyScope> WalkPath(PolicyScope scope) =>
        scope switch
        {
            PolicyScope.Endpoint e => new PolicyScope[]
            {
                e,
                new PolicyScope.Subdomain(e.DomainName, e.SubdomainName),
                new PolicyScope.Domain(e.DomainName),
                new PolicyScope.Wildcard()
            },
            PolicyScope.Subdomain s => new PolicyScope[]
            {
                s,
                new PolicyScope.Domain(s.DomainName),
                new PolicyScope.Wildcard()
            },
            PolicyScope.Domain d => new PolicyScope[]
            {
                d,
                new PolicyScope.Wildcard()
            },
            _ => new PolicyScope[] { new PolicyScope.Wildcard() }
        };

    // Records define value equality, but Equals(object?) on the abstract base
    // dispatches through the closed hierarchy correctly. Keep this as an
    // explicit helper so the intent at call sites stays obvious.
    private static bool ScopeEquals(PolicyScope a, PolicyScope b) => a == b;
}
