namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     Provenance metadata on a materialized <see cref="Rules.PolicyRule"/>.
///     Set when the loader expands a <see cref="Template"/> via a
///     <see cref="TemplateApplication"/>; <c>null</c> for hand-authored bare
///     rules.
///
///     <para>
///         The runtime never reads this field on the request hot path -- it
///         is metadata for the dashboard (group rules under their owning
///         application), for the audit log (trace which template + application
///         emitted a rule), and for T2's override-divergence detection.
///     </para>
/// </summary>
/// <param name="TemplateId">The id of the <see cref="Template"/> that emitted this rule.</param>
/// <param name="ApplicationId">The id of the <see cref="TemplateApplication"/> that emitted this rule.</param>
/// <param name="ExpansionSuffix">
///     The <see cref="TemplateExpansionEntry.Suffix"/> of the blueprint entry
///     that emitted this rule. Combined with <see cref="TemplateId"/> +
///     <see cref="ApplicationId"/> + the rule's scope, this identifies the
///     materialized rule deterministically.
/// </param>
public sealed record RuleOrigin(
    string TemplateId,
    string ApplicationId,
    string ExpansionSuffix);
