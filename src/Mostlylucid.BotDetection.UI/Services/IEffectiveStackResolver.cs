using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     The pair of lists rendered under a single scope group: rules whose
///     <see cref="PolicyRule.Scope"/> equals the queried scope (<see cref="Owned"/>)
///     and rules that actually fire AT the queried scope after inheritance
///     resolution (<see cref="Effective"/>), in evaluation order.
///
///     <para>
///         A1 produces the unsorted-but-correctly-filtered shape; A2 owns the
///         specificity-desc + priority-asc sort, A3 owns the
///         shadowed / overridden / unreachable annotations. Until A3 lands,
///         <see cref="Annotations"/> is always empty.
///     </para>
/// </summary>
/// <param name="Owned">
///     Rule projections whose <see cref="EffectiveRuleProjection.OwningScope"/>
///     equals the queried scope (i.e. <see cref="EffectiveRuleProjection.IsInherited"/>
///     is false). The accordion header tab "Owned" renders this list.
/// </param>
/// <param name="Effective">
///     Every rule that applies at the queried scope after the ancestor walk --
///     ordered as specificity descends from queried-scope down to wildcard.
///     A1 emits natural-input order within the filtered set; A2 replaces this
///     with the specificity-desc sort.
/// </param>
/// <param name="Annotations">
///     Per-rule callouts (shadowed, overridden, unreachable). Empty until A3.
/// </param>
public sealed record EffectiveStackView(
    IReadOnlyList<EffectiveRuleProjection> Owned,
    IReadOnlyList<EffectiveRuleProjection> Effective,
    IReadOnlyList<EffectiveAnnotation> Annotations);

/// <summary>
///     One rule's projection into the Owned/Effective view at a queried scope.
///     The boolean annotation flags (<see cref="IsShadowed"/>,
///     <see cref="IsOverridden"/>, <see cref="IsUnreachable"/>) are always
///     <c>false</c> in A1 -- A3 computes them and re-emits the projection
///     with the right flags set.
/// </summary>
/// <param name="RuleId">
///     Stable <see cref="PolicyRule.Id"/> -- the projection's identity. Kept
///     as <see cref="Guid"/> to match the rest of the policy stack (the plan
///     called this <c>string</c>; the codebase uses <see cref="Guid"/>
///     everywhere so we mirror the existing shape).
/// </param>
/// <param name="Row">
///     Pre-built row view-model for the Razor partial. <c>null</c> in A1
///     because <see cref="EffectiveStackResolver"/> does not yet have the
///     row-builder dependency -- A10 (presenter wiring) populates this slot
///     when it builds the projection. Razor partials that consume
///     <see cref="EffectiveRuleProjection"/> must handle the nullable Row.
/// </param>
/// <param name="OwningScope">
///     The scope the rule is authored under (<see cref="PolicyRule.Scope"/>).
///     Differs from the queried scope when <see cref="IsInherited"/> is true.
/// </param>
/// <param name="IsInherited">
///     <c>true</c> when <see cref="OwningScope"/> is NOT structurally equal
///     to the queried scope -- the rule walks down from an ancestor.
/// </param>
/// <param name="IsShadowed">A3 fills this. Always <c>false</c> in A1.</param>
/// <param name="IsOverridden">A3 fills this. Always <c>false</c> in A1.</param>
/// <param name="IsUnreachable">A3 fills this. Always <c>false</c> in A1.</param>
public sealed record EffectiveRuleProjection(
    Guid RuleId,
    PolicyStackRowViewModel? Row,
    PolicyScope OwningScope,
    bool IsInherited,
    bool IsShadowed,
    bool IsOverridden,
    bool IsUnreachable);

/// <summary>
///     One callout under a rule row. A3 emits these; A1 returns an empty
///     list.
/// </summary>
/// <param name="Kind">
///     Short kind tag -- <c>"shadowed"</c> / <c>"overridden"</c> /
///     <c>"unreachable"</c>. A3 owns the vocabulary.
/// </param>
/// <param name="RuleId">The <see cref="PolicyRule.Id"/> the annotation attaches to.</param>
/// <param name="DetailText">Pre-rendered human-readable detail line.</param>
public sealed record EffectiveAnnotation(string Kind, Guid RuleId, string DetailText);

/// <summary>
///     Resolves the "what rules fire here?" view for a single scope group on
///     the policy editor. Pure function -- no DB / cache / hub dependency --
///     so the presenter can call it inline per scope group.
/// </summary>
public interface IEffectiveStackResolver
{
    /// <summary>
    ///     Build the Owned + Effective view for <paramref name="scope"/>
    ///     given the full rule corpus in <paramref name="allRules"/>. The
    ///     resolver never re-queries the store; the caller passes whatever
    ///     snapshot it already has loaded.
    /// </summary>
    EffectiveStackView ResolveAt(PolicyScope scope, IReadOnlyList<PolicyRule> allRules);
}
