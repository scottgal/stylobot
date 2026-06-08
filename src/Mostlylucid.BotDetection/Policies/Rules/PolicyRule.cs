using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     One authored policy rule: <c>(Scope) IF (Predicate) THEN (Action) AT (Priority) IN (Mode)</c>.
///     The store is the source of truth; the resolver assembles effective
///     rule lists at evaluation time by walking scopes from most-specific to
///     wildcard.
/// </summary>
/// <param name="Id">Stable identifier preserved across reloads.</param>
/// <param name="Scope">Where the rule attaches in the hierarchy.</param>
/// <param name="Priority">Lower number = higher priority within the scope. Ties broken by Id.</param>
/// <param name="Predicate">Parsed predicate AST that gates the action.</param>
/// <param name="Action">Action applied when the predicate evaluates true.</param>
/// <param name="Mode">Lifecycle posture (Draft / Observe / Live / Disabled).</param>
/// <param name="Notes">Human-authored description, never user-facing on the request path.</param>
/// <param name="Source">Provenance string -- e.g. <c>"yaml:domain-default.yaml"</c> or <c>"postgres"</c>.</param>
/// <param name="CreatedAt">Wall-clock time the rule was first loaded into this process.</param>
/// <param name="RevisionId">Re-issued on every reload so consumers can detect "this is the same logical rule, freshly loaded".</param>
public sealed record PolicyRule(
    Guid Id,
    PolicyScope Scope,
    int Priority,
    Predicate.Predicate Predicate,
    PolicyAction Action,
    PolicyMode Mode,
    string Notes,
    string Source,
    DateTimeOffset CreatedAt,
    Guid RevisionId);
