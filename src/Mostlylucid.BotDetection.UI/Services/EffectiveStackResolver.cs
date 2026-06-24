using Mostlylucid.BotDetection.Policies.Predicate;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     A1 implementation. Filters the rule corpus to ancestor-or-equal scopes
///     of the queried scope, projects each match into an
///     <see cref="EffectiveRuleProjection"/>, and splits into the Owned /
///     Effective view-model pair the new Stack tab needs.
///
///     <para>
///         <b>Scope semantics in A1.</b> A rule applies AT the queried scope
///         when every NON-null slot on the rule's scope is structurally equal
///         to the queried scope's slot (treating <see cref="HostScope"/>
///         hierarchy specially: a Domain rule matches a Subdomain or Endpoint
///         under that domain; a Subdomain rule matches an Endpoint under that
///         subdomain).
///     </para>
///
///     <para>
///         <b>A2 sort order.</b> The Effective list is sorted by composite scope
///         specificity descending (most-specific first), then by priority
///         ascending within a tie (Cloudflare convention -- lower priority
///         number wins). This matches the win-order used by
///         <c>DefaultPolicyResolver</c>, so the first card in the Effective tab
///         is the rule that would fire at the queried scope.
///     </para>
///
///     <para>
///         <b>A3 annotation pass.</b> After the A2 sort, walk the Effective list
///         once. The list is already in win-order, so any projection whose
///         predicate hash matches an earlier projection's hash is shadowed by
///         the earlier rule (it would never fire at the queried scope because
///         the more-specific / lower-priority earlier rule wins first). The
///         pass emits <c>"shadowed"</c> annotations and flips
///         <see cref="EffectiveRuleProjection.IsShadowed"/> on the loser.
///     </para>
///
///     <para>
///         <b>What this does NOT do yet.</b>
///         <list type="bullet">
///           <item><description>
///             <c>"overridden"</c>: parameter-override visibility lives in the
///             commercial layer (<c>IConfigOverrideStore</c>); the FOSS-side
///             resolver has no hook to observe overrides, so A3 leaves the
///             <see cref="EffectiveRuleProjection.IsOverridden"/> flag and the
///             override-annotation emission for the commercial resolver / A10.
///           </description></item>
///           <item><description>
///             <c>"unreachable"</c>: requires a per-rule "zero hits in window"
///             signal. <see cref="PolicyRule"/> does not carry hit counts
///             today; A10's row builder is the natural place to fold in
///             telemetry. A3 leaves <see cref="EffectiveRuleProjection.IsUnreachable"/>
///             permanently <c>false</c> until that signal is wired.
///           </description></item>
///           <item><description>
///             Predicate <i>subsumption</i> (e.g. <c>bot.type in (scraper, ai-crawler)</c>
///             shadows <c>bot.type = scraper</c>) is intentionally out of scope:
///             A3 only matches by structural-hash equality via
///             <see cref="PredicateHasher.Hash"/>. Subsumption requires a SAT-ish
///             walk over the AST and lands in a later phase.
///           </description></item>
///           <item><description>Build row view-models -- the presenter (A10) supplies the row builder.</description></item>
///         </list>
///     </para>
/// </summary>
public sealed class EffectiveStackResolver : IEffectiveStackResolver
{
    /// <inheritdoc />
    public EffectiveStackView ResolveAt(PolicyScope scope, IReadOnlyList<PolicyRule> allRules)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(allRules);

        var effective = new List<EffectiveRuleProjection>(allRules.Count);
        var owned = new List<EffectiveRuleProjection>();

        foreach (var rule in allRules)
        {
            if (!IsAncestorOrEqual(rule.Scope, scope)) continue;

            var isInherited = !rule.Scope.Equals(scope);
            var projection = new EffectiveRuleProjection(
                RuleId: rule.Id,
                Row: null, // A10 wires the row builder; A1 leaves this null.
                OwningScope: rule.Scope,
                IsInherited: isInherited,
                IsShadowed: false, // A3 fills this below.
                IsOverridden: false, // A3 leaves false -- see class comment.
                IsUnreachable: false); // A3 leaves false -- see class comment.

            effective.Add(projection);
            if (!isInherited) owned.Add(projection);
        }

        // A2 sort: specificity descending, priority ascending within a tie.
        // We reach back to the originating PolicyRule for the priority key
        // because the projection doesn't carry it (and adding a field is A10's
        // job, not ours). Build a quick id->priority lookup so the sort is
        // O(n log n) rather than O(n^2) on rule scans.
        // The same lookup also keys the A3 annotation pass by rule id, so we
        // build a rule-by-id dictionary once and reuse it.
        var ruleById = new Dictionary<Guid, PolicyRule>(allRules.Count);
        for (var i = 0; i < allRules.Count; i++) ruleById[allRules[i].Id] = allRules[i];

        effective.Sort((a, b) =>
        {
            // Descending specificity: reuse the canonical PolicyScope.Specificity
            // weighted-sum (Endpoint=8, Subdomain=4, Domain=2, Wildcard=0, plus
            // +1 each for Method / Geo / Identity slots). DefaultPolicyResolver
            // uses the same property -- keeping a single source of truth.
            var spec = b.OwningScope.Specificity.CompareTo(a.OwningScope.Specificity);
            if (spec != 0) return spec;
            // Ascending priority: lower number = higher precedence.
            return ruleById[a.RuleId].Priority.CompareTo(ruleById[b.RuleId].Priority);
        });

        // A3 annotation pass: post-sort walk. The list is in win-order, so for
        // each projection at index i, any earlier projection (index < i) with
        // the same predicate hash is the one that actually fires at the
        // queried scope -- this projection is shadowed by that earlier rule.
        //
        // PredicateHasher.Hash is the codebase's canonical structural-equality
        // hash (SustainEvaluator keys atoms by it). We rely on hash equality
        // only; subsumption-based shadowing is deferred (see class summary).
        //
        // Pre-compute hashes once per projection so the inner Take-scan is
        // O(n^2) hash compares instead of O(n^2) full PredicateHasher walks.
        var annotations = new List<EffectiveAnnotation>();
        var hashes = new int[effective.Count];
        for (var i = 0; i < effective.Count; i++)
            hashes[i] = PredicateHasher.Hash(ruleById[effective[i].RuleId].Predicate);

        for (var i = 0; i < effective.Count; i++)
        {
            var hash = hashes[i];
            EffectiveRuleProjection? shadower = null;
            for (var j = 0; j < i; j++)
            {
                if (hashes[j] != hash) continue;
                shadower = effective[j];
                break;
            }

            if (shadower is null) continue;

            effective[i] = effective[i] with { IsShadowed = true };
            annotations.Add(new EffectiveAnnotation(
                Kind: "shadowed",
                RuleId: effective[i].RuleId,
                DetailText: $"shadowed by rule at more-specific scope ({DescribeScope(shadower.OwningScope)})"));
        }

        // Keep the Owned list referentially consistent with Effective: if a
        // post-sort projection was flagged shadowed, the matching Owned entry
        // (added during the initial filter pass) still has IsShadowed=false.
        // Re-sync by replacing each Owned entry with its Effective counterpart
        // by RuleId so consumers reading Owned see the same annotation flags.
        if (annotations.Count > 0 && owned.Count > 0)
        {
            var effectiveById = effective.ToDictionary(p => p.RuleId);
            for (var i = 0; i < owned.Count; i++)
            {
                if (effectiveById.TryGetValue(owned[i].RuleId, out var updated))
                    owned[i] = updated;
            }
        }

        return new EffectiveStackView(
            Owned: owned,
            Effective: effective,
            Annotations: annotations);
    }

    /// <summary>
    ///     Short human-readable scope label used in the
    ///     <see cref="EffectiveAnnotation.DetailText"/> for shadowed rules.
    ///     Mirrors the dashboard's "scope group" labelling so the annotation
    ///     points at a scope the operator can find in the same panel.
    /// </summary>
    private static string DescribeScope(PolicyScope scope) => scope.Host switch
    {
        HostScope.Endpoint e => $"endpoint {e.PathTemplate} on {e.DomainName}",
        HostScope.Subdomain s => string.IsNullOrEmpty(s.SubdomainName)
            ? $"domain {s.DomainName}"
            : $"subdomain {s.SubdomainName}.{s.DomainName}",
        HostScope.Domain d => $"domain {d.Name}",
        _ => "wildcard scope",
    };

    /// <summary>
    ///     True when <paramref name="ruleScope"/> applies at <paramref name="queriedScope"/>:
    ///     every non-null slot on the rule scope is an ancestor-or-equal of the
    ///     corresponding slot on the queried scope. Null slots on the rule
    ///     scope are wildcards and trivially match.
    /// </summary>
    private static bool IsAncestorOrEqual(PolicyScope ruleScope, PolicyScope queriedScope)
    {
        if (ruleScope.Host is not null
            && !HostIsAncestorOrEqual(ruleScope.Host, queriedScope.Host))
            return false;

        if (ruleScope.Method is not null
            && !string.Equals(ruleScope.Method, queriedScope.Method, StringComparison.OrdinalIgnoreCase))
            return false;

        if (ruleScope.Geo is not null
            && !string.Equals(ruleScope.Geo, queriedScope.Geo, StringComparison.OrdinalIgnoreCase))
            return false;

        if (ruleScope.Identity is not null && !ruleScope.Identity.Equals(queriedScope.Identity))
            return false;

        return true;
    }

    /// <summary>
    ///     Host-slot ancestor-or-equal: Domain rule matches a Subdomain or
    ///     Endpoint queried scope under the same domain; Subdomain rule
    ///     matches an Endpoint queried scope under the same subdomain;
    ///     Endpoint rule matches only an identical endpoint.
    /// </summary>
    private static bool HostIsAncestorOrEqual(HostScope ruleHost, HostScope? queriedHost)
    {
        if (queriedHost is null) return false;

        return ruleHost switch
        {
            HostScope.Domain d => queriedHost switch
            {
                HostScope.Domain qd =>
                    string.Equals(d.Name, qd.Name, StringComparison.OrdinalIgnoreCase),
                HostScope.Subdomain qs =>
                    string.Equals(d.Name, qs.DomainName, StringComparison.OrdinalIgnoreCase),
                HostScope.Endpoint qe =>
                    string.Equals(d.Name, qe.DomainName, StringComparison.OrdinalIgnoreCase),
                _ => false,
            },

            HostScope.Subdomain s => queriedHost switch
            {
                HostScope.Subdomain qs =>
                    string.Equals(s.DomainName, qs.DomainName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.SubdomainName, qs.SubdomainName, StringComparison.OrdinalIgnoreCase),
                HostScope.Endpoint qe =>
                    string.Equals(s.DomainName, qe.DomainName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.SubdomainName, qe.SubdomainName, StringComparison.OrdinalIgnoreCase),
                _ => false,
            },

            HostScope.Endpoint e => queriedHost is HostScope.Endpoint qe
                && string.Equals(e.DomainName, qe.DomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.SubdomainName, qe.SubdomainName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.PathTemplate, qe.PathTemplate, StringComparison.Ordinal),

            _ => false,
        };
    }
}
