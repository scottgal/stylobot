namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One template-keyed grouping rendered on the
///     <c>/dashboard/policies</c> view (T4 Part B). Materialized rules are
///     grouped under their template id + application id; hand-authored
///     rules (rules with <c>null</c> <see cref="PolicyStackRowViewModel.Origin"/>)
///     are collected into a single "Manually authored" group identified by
///     <see cref="IsManuallyAuthored"/>.
///
///     <para>
///         The grouping is computed on read by
///         <see cref="Services.PolicyTemplateGrouping"/>; it does not live
///         on the rule store. That keeps the FOSS write surface unchanged
///         and lets the dashboard re-group cheaply on every render.
///     </para>
/// </summary>
/// <param name="TemplateId">
///     Template id this group represents. Empty when
///     <see cref="IsManuallyAuthored"/> is <c>true</c>.
/// </param>
/// <param name="TemplateDisplayName">
///     Operator-facing label. For grouped templates this is the
///     <c>Template.DisplayName</c> when known, otherwise the bare
///     template id. For the manually-authored group this is the
///     localisation-friendly "Manually authored" label.
/// </param>
/// <param name="ApplicationIds">
///     Every distinct <c>application id</c> the rules in this group were
///     emitted from. Multiple applications of the same template appear in
///     the same group, with their ids surfaced here so the dashboard can
///     show one application chip per source.
/// </param>
/// <param name="Rows">
///     Rules belonging to this group, in resolver order (most-specific
///     scope first), so the grouping does not perturb the existing sort.
/// </param>
/// <param name="IsManuallyAuthored">
///     <c>true</c> for the catch-all group covering rules with no
///     <see cref="PolicyStackRowViewModel.Origin"/>. The dashboard renders
///     this group last so operator hand-authored work sits below
///     template-managed groups.
/// </param>
/// <param name="DivergedCount">
///     Count of rules in <see cref="Rows"/> whose
///     <see cref="PolicyStackRowViewModel.IsDiverged"/> is <c>true</c>.
///     Surfaced in the group header so an operator sees at a glance how
///     many rules in this group have drifted from the template.
/// </param>
public sealed record PolicyStackTemplateGroupViewModel(
    string TemplateId,
    string TemplateDisplayName,
    IReadOnlyList<string> ApplicationIds,
    IReadOnlyList<PolicyStackRowViewModel> Rows,
    bool IsManuallyAuthored,
    int DivergedCount);
