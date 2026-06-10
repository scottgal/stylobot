using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;

namespace Mostlylucid.BotDetection.Policies.Resolution;

/// <summary>
///     Default <see cref="IPolicyResolver"/> implementation. Walks the Host
///     slot of a request-shaped <see cref="PolicyScope"/> from most-specific
///     (Endpoint) to broadest (null) and assembles the merged rule list. The
///     orthogonal slots (Method / Geo / Identity) are NOT filtered in
///     <see cref="EffectiveAsync"/> -- they are evaluated by
///     <see cref="EffectiveWithContextAsync"/> via <see cref="PolicyScopeMatcher"/>
///     against the actual per-request signals.
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

        // Build the merged signals map. Three layers, lowest-priority first:
        //   1. Scope-derived request.* / geo.country / identity.* signals from
        //      the `scope` argument (which describes the REQUEST -- Host slot
        //      tells us domain/subdomain/path, orthogonal slots tell us method
        //      / country / identity). These provide defaults so a caller can
        //      omit per-request signals and still get sane scope matching.
        //   2. ISignalContributor entries (Phase F) -- TryAdd semantics so the
        //      contributor never overwrites a value already present.
        //   3. Per-request signals -- always win.
        var merged = new Dictionary<string, object?>(requestSignals.Count + 8);
        FillFromScope(scope, merged);
        if (_contributors.Count > 0)
        {
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
        }
        // Per-request signals overlay last so they win.
        foreach (var kv in requestSignals)
            merged[kv.Key] = kv.Value;
        IReadOnlyDictionary<string, object?> evalSignals = merged;

        // Two-stage filter:
        //   1. PolicyScopeMatcher narrows the URL-walked candidate set by the
        //      orthogonal slots (Method / Geo / Identity). A rule with no
        //      orthogonal slots populated passes through trivially.
        //   2. PredicateEvaluator evaluates the predicate against the merged
        //      signal map -- the existing Phase-F semantics.
        // Output is sorted most-specific-first using PolicyScope.Specificity.
        var result = new List<EffectiveRule>(all.Count);
        foreach (var entry in all)
        {
            if (!PolicyScopeMatcher.MatchesRequest(entry.Rule.Scope, evalSignals)) continue;
            if (!PredicateEvaluator.Evaluate(entry.Rule.Predicate, evalSignals)) continue;
            result.Add(entry);
        }

        result.Sort(static (a, b) =>
        {
            // Descending specificity, then ascending priority within a tie.
            var spec = b.Rule.Scope.Specificity.CompareTo(a.Rule.Scope.Specificity);
            if (spec != 0) return spec;
            return a.Rule.Priority.CompareTo(b.Rule.Priority);
        });

        return result;
    }

    /// <summary>
    ///     Derive the most-specific-first Host-walk for <paramref name="scope"/>.
    ///     The walk only varies on the Host slot; orthogonal slots travel along
    ///     untouched so a request at <c>Endpoint(acme.com, docs, /api/upload)</c>
    ///     pulls rules attached at <c>Endpoint</c>, <c>Subdomain</c>,
    ///     <c>Domain</c>, AND wildcard (no Host slot).
    /// </summary>
    private static IReadOnlyList<PolicyScope> WalkPath(PolicyScope scope)
    {
        // The orthogonal slots on `scope` describe the REQUEST, not the rule.
        // Rule-store lookups index purely by Host -- the orthogonal axes are
        // resolved at predicate-evaluation time. Walk Host only.
        return scope.Host switch
        {
            HostScope.Endpoint e => new PolicyScope[]
            {
                PolicyScope.Endpoint(e.DomainName, e.SubdomainName, e.PathTemplate),
                PolicyScope.Subdomain(e.DomainName, e.SubdomainName),
                PolicyScope.Domain(e.DomainName),
                PolicyScope.Wildcard()
            },
            HostScope.Subdomain s => new PolicyScope[]
            {
                PolicyScope.Subdomain(s.DomainName, s.SubdomainName),
                PolicyScope.Domain(s.DomainName),
                PolicyScope.Wildcard()
            },
            HostScope.Domain d => new PolicyScope[]
            {
                PolicyScope.Domain(d.Name),
                PolicyScope.Wildcard()
            },
            _ => new PolicyScope[] { PolicyScope.Wildcard() }
        };
    }

    // Records define value equality, but Equals(object?) on the abstract base
    // dispatches through the closed hierarchy correctly. Keep this as an
    // explicit helper so the intent at call sites stays obvious.
    private static bool ScopeEquals(PolicyScope a, PolicyScope b) => a == b;

    /// <summary>
    ///     Populate scope-derived request signals so PolicyScopeMatcher can
    ///     filter rules with a Host / Method / Geo / Identity slot. The values
    ///     come from the request-shaped <paramref name="scope"/> the resolver
    ///     was called with. Per-request signals always overlay these later,
    ///     so a caller that DOES populate request.* / geo.country / identity.*
    ///     entries is not overridden.
    /// </summary>
    private static void FillFromScope(PolicyScope scope, IDictionary<string, object?> signals)
    {
        switch (scope.Host)
        {
            case HostScope.Domain d:
                signals[PolicyScopeMatcher.RequestDomainKey] = d.Name;
                break;
            case HostScope.Subdomain s:
                signals[PolicyScopeMatcher.RequestDomainKey] = s.DomainName;
                signals[PolicyScopeMatcher.RequestSubdomainKey] = s.SubdomainName;
                break;
            case HostScope.Endpoint e:
                signals[PolicyScopeMatcher.RequestDomainKey] = e.DomainName;
                signals[PolicyScopeMatcher.RequestSubdomainKey] = e.SubdomainName;
                signals[PolicyScopeMatcher.RequestPathKey] = e.PathTemplate;
                break;
        }
        if (scope.Method is not null) signals[PolicyScopeMatcher.RequestMethodKey] = scope.Method;
        if (scope.Geo is not null) signals[PolicyScopeMatcher.GeoCountryKey] = scope.Geo;
        switch (scope.Identity)
        {
            case IdentityScope.NamedBot n:
                signals[PolicyScopeMatcher.IdentityNamedBotKey] = n.Family;
                break;
            case IdentityScope.BotType b:
                signals[PolicyScopeMatcher.IdentityBotTypeKey] = b.Category;
                break;
            case IdentityScope.HumanBrowser h:
                signals[PolicyScopeMatcher.IdentityHumanBrowserKey] = h.Family;
                break;
            case IdentityScope.Fingerprint f:
                signals[PolicyScopeMatcher.IdentityFingerprintIdKey] = f.Id;
                break;
        }
    }
}
