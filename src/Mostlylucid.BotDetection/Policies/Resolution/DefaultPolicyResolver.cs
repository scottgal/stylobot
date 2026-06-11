using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.Policies.Triggers;

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
///
///     <para>
///         Phase G: an optional <see cref="ArmedRuleRegistry"/> is consulted
///         on every <see cref="EffectiveWithContextAsync"/> call. Armed rules
///         whose <see cref="PolicyScope"/> matches the request scope are
///         merged into the effective list at the TOP (most-specific by
///         construction) with <see cref="EffectiveRule.IsArmed"/> set to
///         <c>true</c>. The resolver leaves the rule's authored
///         <see cref="PolicyRule.Action"/> intact; downstream pipeline
///         components substitute the rule's
///         <see cref="Rules.RuleTriggerOptions.ActionWhileArmed"/> when
///         applying the action. Null registry means "no trigger system in
///         this host" -- typical for viewer-only processes.
///     </para>
/// </summary>
public sealed class DefaultPolicyResolver : IPolicyResolver
{
    private readonly IPolicyRuleStore _store;
    private readonly IReadOnlyList<ISignalContributor> _contributors;
    private readonly ArmedRuleRegistry? _armedRegistry;
    private readonly SustainEvaluator? _sustain;

    /// <summary>
    ///     Construct a resolver that does no signal contribution merging --
    ///     equivalent to the pre-Phase-F behaviour. Preserves the existing DI
    ///     registration shape (TryAddSingleton against the type) for hosts that
    ///     have not registered any contributors yet.
    /// </summary>
    public DefaultPolicyResolver(IPolicyRuleStore store)
        : this(store, contributors: null, armedRegistry: null, sustain: null)
    {
    }

    /// <summary>
    ///     Construct a resolver with an optional contributor set. Null means
    ///     "no contributors" -- the resolver behaves exactly like the
    ///     pre-Phase-F path. A null entry inside the enumerable is filtered.
    /// </summary>
    public DefaultPolicyResolver(IPolicyRuleStore store, IEnumerable<ISignalContributor>? contributors)
        : this(store, contributors, armedRegistry: null, sustain: null)
    {
    }

    /// <summary>
    ///     Phase G constructor: wires the optional
    ///     <see cref="ArmedRuleRegistry"/> so the resolver can merge
    ///     currently-armed rules into the effective list. The DI container
    ///     resolves <paramref name="armedRegistry"/> as nullable; passing
    ///     <c>null</c> leaves the resolver in pre-Phase-G behaviour.
    /// </summary>
    public DefaultPolicyResolver(
        IPolicyRuleStore store,
        IEnumerable<ISignalContributor>? contributors,
        ArmedRuleRegistry? armedRegistry)
        : this(store, contributors, armedRegistry, sustain: null)
    {
    }

    /// <summary>
    ///     T-Expr constructor: also wires the optional
    ///     <see cref="SustainEvaluator"/> so <c>FOR Xs</c> sub-predicates
    ///     consult the shared per-rule sustain atoms. Null means "no sustain
    ///     context" -- the resolver still evaluates Sustain nodes via
    ///     <see cref="PredicateEvaluator.Evaluate(Predicate.Predicate, IReadOnlyDictionary{string, object?}, Guid, SustainEvaluator?)"/>
    ///     but they always degrade to <c>false</c>, which is the documented
    ///     fail-safe.
    /// </summary>
    public DefaultPolicyResolver(
        IPolicyRuleStore store,
        IEnumerable<ISignalContributor>? contributors,
        ArmedRuleRegistry? armedRegistry,
        SustainEvaluator? sustain)
    {
        _store = store;
        _contributors = contributors is null
            ? Array.Empty<ISignalContributor>()
            : contributors.Where(c => c is not null).ToArray();
        _armedRegistry = armedRegistry;
        _sustain = sustain;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EffectiveRule>> EffectiveAsync(
        PolicyScope scope,
        CancellationToken ct = default)
    {
        var path = WalkPath(scope);
        var rules = await _store.GetEffectiveRulesAsync(path, ct).ConfigureAwait(false);

        // Phase G: snapshot the currently-armed rule ids ONCE so the regular
        // walk can stamp IsArmed onto the matching entries and so we know
        // which extra armed rules (attached deeper than the walk path covers)
        // need to be appended.
        HashSet<Guid>? armedIds = null;
        IReadOnlyCollection<PolicyRule>? extraArmedRules = null;
        if (_armedRegistry is not null)
        {
            armedIds = new HashSet<Guid>(_armedRegistry.CurrentlyArmedIds());
            if (armedIds.Count > 0)
                extraArmedRules = await _armedRegistry.CurrentlyArmedAsync(_store, ct).ConfigureAwait(false);
        }

        var result = new List<EffectiveRule>(rules.Count + 4);
        var seenIds = new HashSet<Guid>();
        foreach (var rule in rules)
        {
            seenIds.Add(rule.Id);
            var isArmed = armedIds is not null && armedIds.Contains(rule.Id);
            result.Add(new EffectiveRule(
                rule,
                rule.Scope,
                IsInherited: !ScopeEquals(rule.Scope, scope),
                IsArmed: isArmed));
        }

        // Append armed rules the regular walk missed -- e.g. an armed rule
        // attached at a scope that the request scope's walk path does not
        // include because the host hierarchy is unrelated. Host-attachment
        // narrowing still applies; orthogonal slots fall through to
        // PolicyScopeMatcher in EffectiveWithContextAsync.
        if (extraArmedRules is not null)
        {
            foreach (var rule in extraArmedRules)
            {
                if (!seenIds.Add(rule.Id)) continue;
                if (!ArmedScopeAttachesTo(rule.Scope, scope)) continue;
                result.Add(new EffectiveRule(
                    rule,
                    rule.Scope,
                    IsInherited: !ScopeEquals(rule.Scope, scope),
                    IsArmed: true));
            }
        }

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
        //
        // Phase G: armed rules bypass the predicate check. The trigger service
        // already proved the meter-only predicate was sustained-true; the
        // resolver trusts the armed state and surfaces the rule regardless of
        // the per-request snapshot (per-request signals don't carry the meter
        // facets the predicate references on a viewer host). Scope-matcher
        // still applies so an armed rule attached to a Method/Geo/Identity
        // axis still narrows to those requests.
        var result = new List<EffectiveRule>(all.Count);
        foreach (var entry in all)
        {
            if (!PolicyScopeMatcher.MatchesRequest(entry.Rule.Scope, evalSignals)) continue;
            if (!entry.IsArmed
                && !PredicateEvaluator.Evaluate(entry.Rule.Predicate, evalSignals, entry.Rule.Id, _sustain))
                continue;
            result.Add(entry);
        }

        result.Sort(static (a, b) =>
        {
            // Phase G: armed rules sort first regardless of scope specificity
            // -- they fire because the operator wired them up against a sustained
            // meter condition and the resolver surfaces them above the regular
            // composite-specificity stack.
            if (a.IsArmed != b.IsArmed) return a.IsArmed ? -1 : 1;
            // Descending specificity, then ascending priority within a tie.
            var spec = b.Rule.Scope.Specificity.CompareTo(a.Rule.Scope.Specificity);
            if (spec != 0) return spec;
            return a.Rule.Priority.CompareTo(b.Rule.Priority);
        });

        return result;
    }

    /// <summary>
    ///     <c>true</c> when <paramref name="ruleScope"/>'s Host slot attaches
    ///     to <paramref name="requestScope"/>. Wildcard host always attaches;
    ///     a Domain rule attaches to any request on that domain; a Subdomain
    ///     rule attaches when both domain and subdomain match; an Endpoint
    ///     rule attaches when domain, subdomain and path all match.
    ///
    ///     <para>
    ///         Orthogonal slots (Method / Geo / Identity) are NOT filtered
    ///         here -- they fall through to <see cref="PolicyScopeMatcher.MatchesRequest"/>
    ///         in <see cref="EffectiveWithContextAsync"/>.
    ///     </para>
    /// </summary>
    private static bool ArmedScopeAttachesTo(PolicyScope ruleScope, PolicyScope requestScope)
    {
        // Host slot: walk the rule's host against the request's host hierarchy.
        if (ruleScope.Host is null) return true;
        return ruleScope.Host switch
        {
            HostScope.Domain d => requestScope.Host switch
            {
                HostScope.Domain rd => string.Equals(d.Name, rd.Name, StringComparison.OrdinalIgnoreCase),
                HostScope.Subdomain rs => string.Equals(d.Name, rs.DomainName, StringComparison.OrdinalIgnoreCase),
                HostScope.Endpoint re => string.Equals(d.Name, re.DomainName, StringComparison.OrdinalIgnoreCase),
                _ => false
            },
            HostScope.Subdomain s => requestScope.Host switch
            {
                HostScope.Subdomain rs =>
                    string.Equals(s.DomainName, rs.DomainName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.SubdomainName, rs.SubdomainName, StringComparison.OrdinalIgnoreCase),
                HostScope.Endpoint re =>
                    string.Equals(s.DomainName, re.DomainName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.SubdomainName, re.SubdomainName, StringComparison.OrdinalIgnoreCase),
                _ => false
            },
            HostScope.Endpoint e => requestScope.Host is HostScope.Endpoint re
                && string.Equals(e.DomainName, re.DomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.SubdomainName, re.SubdomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.PathTemplate, re.PathTemplate, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
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
