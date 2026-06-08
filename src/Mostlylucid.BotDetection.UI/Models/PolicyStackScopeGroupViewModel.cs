using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One scope-level grouping rendered by the Stack tab. The Stack tab
///     orders groups ancestor-first (Wildcard → Domain → Subdomain →
///     Endpoint) so the operator reads left-to-right exactly the way the
///     resolver builds the chain. Rule rows inside the group are
///     ALREADY-built <see cref="PolicyStackRowViewModel"/> instances --
///     reusing the compact <c>_RuleRow</c> partial from the Effective tab
///     is the whole point of the layered design.
/// </summary>
/// <param name="Scope">The scope this group represents.</param>
/// <param name="ScopeLabel">Display label -- <c>"DOMAIN  acme.com"</c>, <c>"GLOBAL"</c> etc.</param>
/// <param name="Specificity">Mirrors <see cref="PolicyScope.Specificity"/> -- 0 wildcard, 3 endpoint.</param>
/// <param name="Rows">Pre-built rule rows attached to this scope, in resolver order.</param>
/// <param name="Conflicts">Conflicts whose owner is this scope; rendered as callouts after the rows.</param>
public sealed record PolicyStackScopeGroupViewModel(
    PolicyScope Scope,
    string ScopeLabel,
    int Specificity,
    IReadOnlyList<PolicyStackRowViewModel> Rows,
    IReadOnlyList<PolicyConflictViewModel> Conflicts);
