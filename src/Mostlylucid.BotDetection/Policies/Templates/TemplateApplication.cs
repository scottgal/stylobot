using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     An operator's request to apply a <see cref="Template"/>: pick a
///     template id, supply parameter values, attach to one or more composite
///     scopes. Compiles to (parameters x expansion x applied-to) concrete
///     <see cref="Rules.PolicyRule"/> instances via
///     <see cref="TemplateResolver"/>.
/// </summary>
/// <param name="Id">
///     Customer-scoped application id (e.g. <c>"my-login-protection"</c>).
///     Stamped onto every materialized rule's <see cref="RuleOrigin"/> so the
///     dashboard can group rules back to their application + the audit log
///     can trace edits.
/// </param>
/// <param name="TemplateId">Reference to the <see cref="Template.Id"/> being applied.</param>
/// <param name="AppliedTo">
///     One or more composite scopes the template's expansion attaches to. An
///     empty list is interpreted as a single wildcard scope so templates
///     designed for global application (e.g. <c>keep-verified-bots-working</c>)
///     do not need a synthetic wildcard entry in their YAML.
/// </param>
/// <param name="Parameters">
///     Parameter values keyed by parameter name. Application values win over
///     the template's parameter defaults. Lookups are case-insensitive.
/// </param>
public sealed record TemplateApplication(
    string Id,
    string TemplateId,
    IReadOnlyList<PolicyScope> AppliedTo,
    IReadOnlyDictionary<string, object?> Parameters);
