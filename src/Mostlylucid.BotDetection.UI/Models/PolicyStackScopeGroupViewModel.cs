using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One scope-level grouping rendered by the Stack tab. The Stack tab
///     orders groups ancestor-first (Wildcard → Domain → Subdomain →
///     Endpoint) so the operator reads left-to-right exactly the way the
///     resolver builds the chain. Rule rows inside the group are
///     ALREADY-built <see cref="PolicyStackRowViewModel"/> instances --
///     reusing the compact <c>_RuleCard</c> partial from the Effective tab
///     is the whole point of the layered design.
///
///     <para>
///         A7 extends the record with the Owned / Effective split that the
///         accordion + tab UI demands. The legacy positional <c>Rows</c> /
///         <c>Conflicts</c> arguments stay first so existing presenter call
///         sites keep compiling unchanged; the A1-A3 outputs ride on the
///         trailing init-only members which the presenter (A10) will
///         populate once it threads <see cref="EffectiveStackResolver"/>
///         per group. Until then those members default to empty lists and
///         the new Razor partial renders the placeholders gracefully.
///     </para>
/// </summary>
/// <param name="Scope">The scope this group represents.</param>
/// <param name="ScopeLabel">Display label -- <c>"DOMAIN  acme.com"</c>, <c>"GLOBAL"</c> etc. Composite header form retained for callers that still consume the old shape.</param>
/// <param name="Specificity">Mirrors <see cref="PolicyScope.Specificity"/> -- 0 wildcard, 3 endpoint.</param>
/// <param name="Rows">Pre-built rule rows attached to this scope, in resolver order. Same content as <see cref="OwnedRules"/> for groups built before A10; kept positional for back-compat.</param>
/// <param name="Conflicts">Conflicts whose owner is this scope; rendered as callouts after the rows.</param>
public sealed record PolicyStackScopeGroupViewModel(
    PolicyScope Scope,
    string ScopeLabel,
    int Specificity,
    IReadOnlyList<PolicyStackRowViewModel> Rows,
    IReadOnlyList<PolicyConflictViewModel> Conflicts)
{
    /// <summary>
    ///     Short type-only label for the accordion header -- <c>"Wildcard"</c>,
    ///     <c>"Domain"</c>, <c>"Subdomain"</c>, <c>"Endpoint"</c>. The
    ///     presenter owns the vocabulary so the view never hand-rolls a
    ///     scope-kind string table.
    /// </summary>
    public string ScopeTypeLabel { get; init; } = StackScopeKindLabel(Scope);

    /// <summary>
    ///     Headline (operator-readable) projection of <see cref="Scope"/> --
    ///     the same string A4's <c>_RuleCard</c> uses for the per-rule
    ///     target. The accordion header pairs <see cref="ScopeTypeLabel"/>
    ///     with this to show the kind + the actual host/path.
    /// </summary>
    public string ScopeTarget { get; init; } = PolicyScopeFormatter.ToHeadline(Scope);

    /// <summary>
    ///     Optional one-liner under the header. Presenter sets this for
    ///     groups that need extra context (e.g. wildcard groups read
    ///     "applies to every request"); empty string suppresses the line
    ///     in the view.
    /// </summary>
    public string ScopeDescription { get; init; } = string.Empty;

    /// <summary>
    ///     Count of <see cref="OwnedRules"/> -- broken out so the header
    ///     can render <c>"(N owned · M effective)"</c> without consumers
    ///     having to enumerate the lists at render time.
    /// </summary>
    public int OwnedCount { get; init; } = Rows.Count;

    /// <summary>
    ///     Count of <see cref="EffectiveRules"/>. A10 populates this once
    ///     the presenter walks <see cref="IEffectiveStackResolver"/> per
    ///     group; today it falls back to <see cref="OwnedCount"/> so the
    ///     header reads consistently while the resolver is unwired.
    /// </summary>
    public int EffectiveCount { get; init; } = Rows.Count;

    /// <summary>
    ///     Rules whose <see cref="PolicyRule.Scope"/> equals this group's
    ///     scope -- the Owned tab body. Defaults to <see cref="Rows"/> so
    ///     the existing presenter output flows through the new tab markup
    ///     without modification until A10 swaps in the resolver-built
    ///     projections.
    /// </summary>
    public IReadOnlyList<PolicyStackRowViewModel> OwnedRules { get; init; } = Rows;

    /// <summary>
    ///     Inheritance rollup -- every rule that fires at this group's
    ///     scope, in evaluation order, after walking the ancestor chain.
    ///     A10 populates this from <see cref="IEffectiveStackResolver"/>;
    ///     until then defaults to an empty list and the Effective panel
    ///     renders the "No effective rules at this scope" placeholder.
    /// </summary>
    public IReadOnlyList<EffectiveRuleProjection> EffectiveRules { get; init; }
        = Array.Empty<EffectiveRuleProjection>();

    /// <summary>
    ///     Per-rule annotations (shadowed / overridden / unreachable)
    ///     emitted by the resolver. Empty until A10 wires the call.
    /// </summary>
    public IReadOnlyList<EffectiveAnnotation> Annotations { get; init; }
        = Array.Empty<EffectiveAnnotation>();

    /// <summary>
    ///     Initial accordion state. The Stack tab today always opens every
    ///     group expanded so the operator can scan the chain top-to-bottom;
    ///     a future surface (per-rule deep-link, "narrow to endpoint")
    ///     may flip selected groups closed. Default <c>false</c>.
    /// </summary>
    public bool DefaultCollapsed { get; init; }

    /// <summary>
    ///     Gate for the per-scope "+ Add rule" affordance. False today because
    ///     the actual add-rule editor (curated facet picker + inline expand
    ///     surface) lands in Phase B (task B8). The polish pass hides the
    ///     dead anchor by default; Phase B flips this to <c>true</c> once the
    ///     editor is wired so the link goes somewhere real.
    /// </summary>
    public bool AddRuleEnabled { get; init; }

    private static string StackScopeKindLabel(PolicyScope scope) => scope.Host switch
    {
        null => "Wildcard",
        HostScope.Domain => "Domain",
        HostScope.Subdomain => "Subdomain",
        HostScope.Endpoint => "Endpoint",
        _ => "Wildcard",
    };
}
