using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     Default <see cref="IPolicyResolver"/> implementation. Walks the scope
///     chain (<see cref="PolicyScope.Endpoint"/> →
///     <see cref="PolicyScope.Subdomain"/> →
///     <see cref="PolicyScope.Domain"/> →
///     <see cref="PolicyScope.Wildcard"/>) and asks the underlying
///     <see cref="IPolicyRuleStore"/> for the concatenated stack. Predicate
///     filtering is delegated to <see cref="PredicateEvaluator"/>.
///
///     <para>
///         Phase F: an optional <see cref="ISignalContributor" /> set is
///         enumerated once per <see cref="EffectiveWithContextAsync" /> call
///         and merges its output on top of the per-request signal bag.
///         Per-request signals always win (TryAdd semantics on the
///         contributor side). A faulty contributor MUST NOT take down policy
///         resolution: non-cancellation exceptions are swallowed,
///         <see cref="OperationCanceledException" /> is re-thrown.
///     </para>
/// </summary>
public sealed class DefaultPolicyResolver : IPolicyResolver
{
    private readonly IPolicyRuleStore _store;
    private readonly IReadOnlyList<ISignalContributor> _contributors;

    /// <summary>
    ///     Construct a resolver that does no signal contribution merging --
    ///     equivalent to the pre-Phase-F behaviour. Preserves the existing DI
    ///     registration shape (TryAddSingleton against the type) for hosts that
    ///     have not registered any contributors yet.
    /// </summary>
    public DefaultPolicyResolver(IPolicyRuleStore store)
        : this(store, contributors: null)
    {
    }

    /// <summary>
    ///     Construct a resolver with an optional contributor set. Null means
    ///     "no contributors" -- the resolver behaves exactly like the
    ///     pre-Phase-F path. A null entry inside the enumerable is filtered.
    /// </summary>
    public DefaultPolicyResolver(IPolicyRuleStore store, IEnumerable<ISignalContributor>? contributors)
    {
        _store = store;
        _contributors = contributors is null
            ? Array.Empty<ISignalContributor>()
            : contributors.Where(c => c is not null).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(
        PolicyScope scope,
        CancellationToken ct = default)
    {
        var path = WalkPath(scope);
        var rules = await _store.GetEffectiveRulesAsync(path, ct).ConfigureAwait(false);

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

        // Build the merged signals map once: per-request signals first (they win),
        // contributors add anything else with TryAdd semantics. When no contributor
        // is wired we hand the original dict to PredicateEvaluator unchanged so the
        // hot path stays allocation-equivalent to the pre-Phase-F behaviour.
        IReadOnlyDictionary<string, object?> evalSignals;
        if (_contributors.Count == 0)
        {
            evalSignals = requestSignals;
        }
        else
        {
            var merged = new Dictionary<string, object?>(requestSignals);
            foreach (var contributor in _contributors)
            {
                try
                {
                    await contributor.ContributeAsync(merged, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A faulty contributor MUST NOT take down policy resolution.
                    // Log when an ILogger is plumbed in; silent for now so the
                    // resolver stays a pure dependency graph.
                }
            }
            evalSignals = merged;
        }

        var result = new List<EffectiveRule>(all.Count);
        foreach (var entry in all)
        {
            if (PredicateEvaluator.Evaluate(entry.Rule.Predicate, evalSignals))
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
