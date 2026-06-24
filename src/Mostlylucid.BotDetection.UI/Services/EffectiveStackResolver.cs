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
///         <b>What this does NOT do yet.</b>
///         <list type="bullet">
///           <item><description>Sort the Effective list -- A2 sorts by specificity desc, then priority asc, then id.</description></item>
///           <item><description>Compute shadowed/overridden/unreachable annotations -- A3 owns that.</description></item>
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
                IsShadowed: false, // A3
                IsOverridden: false, // A3
                IsUnreachable: false); // A3

            effective.Add(projection);
            if (!isInherited) owned.Add(projection);
        }

        return new EffectiveStackView(
            Owned: owned,
            Effective: effective,
            Annotations: Array.Empty<EffectiveAnnotation>());
    }

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
