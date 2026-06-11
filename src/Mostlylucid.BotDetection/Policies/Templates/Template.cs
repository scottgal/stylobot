namespace Mostlylucid.BotDetection.Policies.Templates;

/// <summary>
///     A parameterized intent that compiles to N bare <see cref="Rules.PolicyRule"/>
///     instances at YAML load time. Stylobot ships a canonical catalog under
///     <c>Templates/Catalog/*.yaml</c>; customers extend with the same schema in
///     their own YAML files (T5 adds the <c>extends:</c> inheritance semantics).
///
///     <para>
///         A template is loader-time sugar. Once a
///         <see cref="TemplateApplication"/> selects a template and supplies
///         parameter values, the <see cref="TemplateResolver"/> expands the pair
///         into concrete bare <see cref="Rules.PolicyRule"/> instances tagged
///         with <see cref="RuleOrigin"/>. The runtime resolver / dispatcher /
///         trigger service never sees a Template or TemplateApplication --
///         everything past the loader is a flat rule list.
///     </para>
/// </summary>
/// <param name="Id">
///     Stable id, customer-globally unique. Used as the lookup key in
///     <see cref="TemplateRegistry"/> and as part of the
///     <see cref="RuleOrigin.TemplateId"/> stamped on every materialized rule.
/// </param>
/// <param name="DisplayName">Operator-facing label rendered in the dashboard template picker.</param>
/// <param name="Description">One-line summary of operator intent.</param>
/// <param name="Category">
///     Grouping label for the template picker (e.g. <c>endpoint-protection</c>,
///     <c>overload</c>, <c>verified-bots</c>).
/// </param>
/// <param name="Parameters">
///     Declared parameters the template accepts. Each parameter has a name,
///     type, optional default, and optional description. Application-supplied
///     values override defaults; missing application values fall through to
///     the default.
/// </param>
/// <param name="Expansion">
///     Ordered list of rule blueprints. Each entry compiles to one
///     materialized rule per applied-to scope. Suffix is unique within the
///     template so re-applications produce deterministic ids.
///
///     <para>
///         For a template with <see cref="ExtendsTemplateId"/> set, the
///         expansion list AS-AUTHORED holds only the child's added entries;
///         the registry constructor merges parent + child at load time and
///         stores the FULLY-RESOLVED template (parent's entries + child's).
///         Consumers (<see cref="TemplateResolver"/>, materializer, editor)
///         always see the resolved shape.
///     </para>
/// </param>
/// <param name="ExtendsTemplateId">
///     Optional id of the parent template this one inherits from. Set in the
///     YAML via <c>extends:</c>. At <see cref="TemplateRegistry"/>
///     construction time the inheritance graph is resolved: parameters are
///     merged (child overrides parent on name match), expansion entries are
///     concatenated (parent first, child's appended). The flattened template
///     stored in the registry has <see cref="ExtendsTemplateId"/> set to
///     <c>null</c> -- runtime consumers see a single-source record.
///
///     <para>
///         <c>null</c> for the common case (Stylobot catalog templates +
///         single-source customer templates).
///     </para>
/// </param>
public sealed record Template(
    string Id,
    string DisplayName,
    string Description,
    string Category,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyList<TemplateExpansionEntry> Expansion,
    string? ExtendsTemplateId = null);

/// <summary>
///     Declared parameter on a <see cref="Template"/>. Parameters are
///     substituted into the expansion entries' predicate and action text at
///     resolution time.
/// </summary>
/// <param name="Name">
///     Parameter name as it appears in the <c>{{ name }}</c> placeholder.
///     Matched case-insensitively at substitution time.
/// </param>
/// <param name="Type">
///     One of <c>"int"</c>, <c>"number"</c>, <c>"string"</c>,
///     <c>"duration"</c>, <c>"bool"</c>. Carried as metadata only at this
///     stage -- the dashboard editor (T2+) uses it to pick the input control;
///     the resolver does string substitution regardless.
/// </param>
/// <param name="Default">Default value used when the application does not supply one; null = required.</param>
/// <param name="Description">Optional operator-facing description.</param>
public sealed record TemplateParameter(
    string Name,
    string Type,
    object? Default,
    string? Description = null);

/// <summary>
///     One blueprint rule inside a <see cref="Template"/>. The expansion list
///     models the "n rules per template" shape: applying a template emits one
///     concrete <see cref="Rules.PolicyRule"/> per
///     <see cref="TemplateExpansionEntry"/> per applied-to scope.
/// </summary>
/// <param name="Suffix">
///     Identifier suffix used to build the materialized rule's stable id and
///     <see cref="RuleOrigin.ExpansionSuffix"/> stamp. Must be unique within
///     the template (the resolver does not de-dup).
/// </param>
/// <param name="Predicate">
///     Raw predicate text in the policy expression language with
///     <c>{{ parameter }}</c> placeholders. Substituted then parsed via
///     <see cref="Predicate.PredicateParser.Parse"/> at resolution time.
/// </param>
/// <param name="Action">
///     Raw action body with <c>{{ parameter }}</c> placeholders. Shape:
///     <c>"{ kind: rate_limit, rpm: 6 }"</c>. Substituted then parsed by
///     <see cref="ActionBodyParser"/> at resolution time.
/// </param>
/// <param name="Priority">Lower number = higher priority within the scope.</param>
/// <param name="Mode">
///     Lifecycle posture as a string: <c>"draft"</c>, <c>"observe"</c>, or
///     <c>"live"</c>. Mapped to <see cref="Rules.PolicyMode"/> at resolution
///     time. Unrecognised values fall through to <see cref="Rules.PolicyMode.Live"/>.
/// </param>
public sealed record TemplateExpansionEntry(
    string Suffix,
    string Predicate,
    string Action,
    int Priority,
    string Mode);
