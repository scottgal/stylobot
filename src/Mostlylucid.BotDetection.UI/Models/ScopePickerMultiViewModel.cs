using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Traffic-shaping plan Task 10 -- view model for the multi-scope
///     wrapper around the existing <c>SbScopePicker</c> view component.
///     The Apply Template flow (Spec 2026-06-10) lets an operator declare
///     N independent scopes per application (e.g. "apply <c>protect-login</c>
///     to <c>api.acme.com / POST /login</c> AND
///     <c>api.acme.com / POST /oauth/*</c>"). Each row is an independent
///     <see cref="SbScopePicker"/> instance with its own hidden-input
///     <c>FieldName</c> drawn from <see cref="FieldNamePrefix"/> + the row
///     index so a single posted form binds N distinct
///     <see cref="PolicyScope"/> payloads.
/// </summary>
/// <param name="InitialScopes">
///     One entry per row; <c>null</c> entries render as wildcard
///     pickers (every axis "none") for the operator to fill in. An empty
///     list is treated as a single empty row by the rendering middleware
///     so the operator always has at least one picker to populate.
/// </param>
/// <param name="FieldNamePrefix">
///     The hidden-input name prefix; each row renders as
///     <c>{FieldNamePrefix}[i]</c> (e.g. <c>applied-to[0]</c>,
///     <c>applied-to[1]</c>, ...) so model-binders pick up the rows as an
///     ordered list. Defaults to <c>applied-to</c> at the middleware
///     layer when the caller doesn't supply a prefix.
/// </param>
public sealed record ScopePickerMultiViewModel(
    IReadOnlyList<PolicyScope?> InitialScopes,
    string FieldNamePrefix);