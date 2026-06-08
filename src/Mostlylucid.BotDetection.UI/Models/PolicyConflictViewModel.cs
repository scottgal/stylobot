using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One candidate override conflict surfaced by
///     <see cref="Services.PolicyConflictAnalyzer"/>. The Stack tab renders
///     these as inline callouts under the OWNER scope's group so the
///     operator can see which ancestor verdict the local rule shadows
///     and follow up in the explainer (B5).
/// </summary>
/// <param name="OwnerRuleId">The rule that overrides -- the one whose scope owns the callout.</param>
/// <param name="OverriddenRuleId">The ancestor rule whose verdict gets shadowed.</param>
/// <param name="OwnerScope">Scope of <paramref name="OwnerRuleId"/>.</param>
/// <param name="OverriddenScope">Scope of <paramref name="OverriddenRuleId"/>.</param>
/// <param name="Severity">Canonical dashboard severity -- <c>"warning"</c> or <c>"info"</c>.</param>
/// <param name="Headline">One-line text shown inline in the callout.</param>
/// <param name="Detail">Long-form explanation for tooltip / future drawer.</param>
public sealed record PolicyConflictViewModel(
    Guid OwnerRuleId,
    Guid OverriddenRuleId,
    PolicyScope OwnerScope,
    PolicyScope OverriddenScope,
    string Severity,
    string Headline,
    string Detail);
