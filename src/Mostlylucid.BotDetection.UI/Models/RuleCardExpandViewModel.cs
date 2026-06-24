using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>_RuleCardExpand.cshtml</c> -- the inline edit
///     surface the operator sees when they click the pencil on a
///     <see cref="PolicyStackRowViewModel"/> card. The expand wraps the
///     existing <see cref="PolicyEditRowViewModel"/> editor mechanics
///     (chip / textarea / per-kind action editor) with card-shaped chrome:
///     header (priority # + scope target + Save/Cancel buttons), curated
///     facet picker above the predicate editor, and an inline hysteresis
///     disclosure below.
///
///     <para>
///         The wrapped <see cref="Editor"/> carries everything the existing
///         predicate / action editor JS already knows how to drive --
///         keeping the canonical form-encoded names (<c>action.kind</c>,
///         <c>predicate</c>, <c>mode</c>, <c>notes</c>) so the same
///         <c>PUT /api/v1/policies/{id}</c> wire shape still works without
///         touching the commercial mutation API.
///     </para>
/// </summary>
/// <param name="Editor">
///     The inner editor view-model the existing chip / textarea / action
///     panes consume. Reuse via <c>@await Html.PartialAsync(...)</c> in the
///     card's body keeps the editor canonical and avoids duplicating the
///     predicate-editor wiring (chip pane, textarea pane, action editor
///     slot, HTMX backtest panel).
/// </param>
/// <param name="ScopeTarget">
///     Webmaster-readable headline form of <see cref="PolicyEditRowViewModel.Scope"/>,
///     pre-formatted by <see cref="Services.PolicyScopeFormatter.ToHeadline"/>.
///     Renders in the card header next to the priority number.
/// </param>
/// <param name="Action">
///     Current action (so the header can read its kind for the dropdown
///     binding) or <c>null</c> for a brand-new rule.
/// </param>
/// <param name="AutoPromoteAt">
///     Optional auto-promote timestamp. Surfaced in the meta band of the
///     compact card today; threaded through here so the expand surface can
///     show + clear it on save without re-fetching the rule.
/// </param>
/// <param name="Trigger">
///     Optional <see cref="RuleTriggerOptions"/> -- when non-null the
///     hysteresis &lt;details&gt; opens by default and the sustain / recover
///     inputs are pre-filled from <see cref="RuleTriggerOptions.SustainFor"/>
///     and <see cref="RuleTriggerOptions.RecoverAfter"/>.
/// </param>
/// <param name="PickerRows">
///     Initial state for the curated facet picker. Empty when the existing
///     predicate AST doesn't project cleanly into a flat AND-of-Terms shape
///     (mixed OR / nested groups); the raw-DSL textarea still carries the
///     canonical predicate text in that case.
/// </param>
public sealed record RuleCardExpandViewModel(
    PolicyEditRowViewModel Editor,
    string ScopeTarget,
    PolicyAction? Action,
    DateTimeOffset? AutoPromoteAt,
    RuleTriggerOptions? Trigger,
    IReadOnlyList<FacetPickerRowViewModel> PickerRows);
