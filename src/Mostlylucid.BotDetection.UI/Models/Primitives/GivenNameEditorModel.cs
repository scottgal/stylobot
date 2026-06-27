namespace Mostlylucid.BotDetection.UI.Models.Primitives;

/// <summary>
///     View model for the inline operator name editor (<c>_GivenNameInlineEditor.cshtml</c>).
///     Rendered by the commercial editor GET endpoint and swapped into the dashboard's
///     <c>.fp-name</c> wrapper via HTMX. The partial is FOSS so a paid plugin host can
///     drive the swap without leaking commercial Razor onto unlicensed gateways; the GET
///     endpoint that produces it lives in <c>Stylobot.Commercial.GatewayPlugin.Editors</c>
///     and is mounted only when <c>MapCommercialEditorEndpoints()</c> is invoked.
///
///     Spec: <c>docs/superpowers/specs/2026-06-27-fingerprint-name-slots-editor-demo-mode-design.md</c>
///     §7 (operator editor surface) + §6.3 (demo-mode UI affordances).
/// </summary>
/// <param name="FingerprintId">The fingerprint whose given-name slot is being edited.</param>
/// <param name="CurrentGivenName">
///     The current operator-pinned name, or <c>null</c> when the slot is empty (resolver
///     falls through to <c>llm ?? induced</c>). Pre-fills the input.
/// </param>
/// <param name="PostUrl">
///     Absolute URL of the POST endpoint that persists the name. Pre-built by the caller
///     so the partial does no URL composition.
/// </param>
/// <param name="CancelUrl">
///     Absolute URL of the GET endpoint that re-renders the read-only display when Cancel
///     is clicked. HTMX swaps it back into the same <c>.fp-name</c> wrapper.
/// </param>
/// <param name="MaxLength">
///     Raw character cap (server enforces too via <c>GivenNameEditorOptions.GivenNameMaxLength</c>).
///     Wired to the <c>maxlength</c> attribute so the browser cuts the operator off before
///     the POST round-trip.
/// </param>
public sealed record GivenNameEditorModel(
    string FingerprintId,
    string? CurrentGivenName,
    string PostUrl,
    string CancelUrl,
    int MaxLength);
