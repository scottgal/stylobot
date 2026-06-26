using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top-level view model for the <c>SbPolicyStack</c> view component. The
///     presenter pre-builds every signal the Razor partials need so the
///     templates do not call back into services. <see cref="Rows"/> is
///     ordered most-specific-source-scope first; the empty-state is handled
///     by checking <see cref="Rows"/>.<see cref="IReadOnlyList{T}.Count"/>.
/// </summary>
/// <param name="Scope">The scope the consumer queried -- drives the breadcrumb's active node.</param>
/// <param name="BreadcrumbPath">Wildcard → Domain → Subdomain → Endpoint, in that order, terminating at <paramref name="Scope"/>.</param>
/// <param name="Embed">Which embed shape the view component was asked to render.</param>
/// <param name="ActiveTab">Active tab name for the Full embed -- <c>"effective"</c> or <c>"stack"</c>.</param>
/// <param name="AggregateWindow">Sliding window the aggregates cover. Default 24h.</param>
/// <param name="CanEdit">Hint that the caller has commercial+role to edit. B1+B2 threads but does not render edit affordances.</param>
/// <param name="Rows">Pre-built compact rule rows, most-specific first.</param>
/// <param name="TotalEffectiveRules">Count of effective rules at <paramref name="Scope"/> -- mirrors <see cref="Rows"/>.<see cref="IReadOnlyList{T}.Count"/>.</param>
/// <param name="RulesTriggeredInWindow">Number of rows whose <see cref="PolicyStackRowViewModel.TriggerCount"/> &gt; 0.</param>
/// <param name="LatestEditAt">Max <see cref="PolicyRule.CreatedAt"/> across the effective rules; null when no rules.</param>
/// <param name="ScopeHash">Stable 16-hex-char SHA-256 prefix of the scope. B6 uses this as the SignalR group name.</param>
/// <param name="StackGroups">Ancestor-first scope groupings for the Stack tab. Empty when the Stack tab isn't active.</param>
/// <param name="Conflicts">Flat list of every conflict produced by the analyzer; per-group copies live on each <see cref="PolicyStackScopeGroupViewModel.Conflicts"/>.</param>
/// <param name="Filter">Parsed filter state from <c>?policystack-filter=</c>; <see cref="PolicyStackFilter.Empty"/> when no filter is active.</param>
/// <param name="Sort">Parsed sort state from <c>?policystack-sort=/?policystack-dir=</c>; <see cref="PolicyStackSort.Default"/> when not specified.</param>
/// <param name="AggregateStrip">Aggregate strip summarising the post-filter visible row set; <c>null</c> for <see cref="PolicyStackEmbed.StatusBadge"/>.</param>
/// <param name="Explainer">Pre-built explainer panel state when the presenter was given a fingerprint to replay; <c>null</c> otherwise. Surfaced on the Full embed and on EffectiveOnly when <see cref="PolicyExplainerViewModel.Locked"/>.</param>
/// <param name="HideRuleList">
///     When <c>true</c>, the Full embed renders its filter bar / aggregate
///     strip / explainer panel without the underlying rule list. Set by the
///     <c>/dashboard/policies</c> page where the T4 template-grouped block
///     above the component already renders every effective rule -- without
///     this flag the page would render every rule twice. Defaults to
///     <c>false</c> so the other 9 SbPolicyStack call sites are unchanged.
/// </param>
/// <param name="EffectiveRulesSnapshot">
///     Flat list of every <see cref="PolicyRule"/> effective at <see cref="Scope"/>
///     -- the underlying rule objects behind <see cref="Rows"/>. Threaded
///     through so the C15 posture view component can call
///     <c>PolicyStackPostureClassifier.Classify</c> without going back to the
///     resolver. Empty array on the <see cref="PolicyStackEmbed.StatusBadge"/>
///     embed (which intentionally skips the per-rule fan-out).
/// </param>
public sealed record PolicyStackViewModel(
    PolicyScope Scope,
    IReadOnlyList<PolicyScope> BreadcrumbPath,
    PolicyStackEmbed Embed,
    string ActiveTab,
    TimeSpan AggregateWindow,
    bool CanEdit,
    IReadOnlyList<PolicyStackRowViewModel> Rows,
    int TotalEffectiveRules,
    int RulesTriggeredInWindow,
    DateTimeOffset? LatestEditAt,
    string ScopeHash,
    IReadOnlyList<PolicyStackScopeGroupViewModel> StackGroups,
    IReadOnlyList<PolicyConflictViewModel> Conflicts,
    PolicyStackFilter Filter,
    PolicyStackSort Sort,
    PolicyStackAggregateStrip? AggregateStrip,
    PolicyExplainerViewModel? Explainer = null,
    bool HideRuleList = false,
    IReadOnlyList<PolicyRule>? EffectiveRulesSnapshot = null);
