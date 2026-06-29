namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Model for the <c>_CidrInput.cshtml</c> partial inside the curated facet
///     picker (<c>_FacetPicker.cshtml</c>). Rendered when a catalog entry has
///     <c>type: cidr</c> (see <c>picker-catalog.yaml</c> -- the <c>ip.address</c>
///     facet uses this).
///     <para>
///         The partial is presentation only: it collects a CIDR string (or a
///         comma-separated list) from the operator and emits a client-side
///         validation hint. The submit / apply pipeline that consumes the
///         value is commercial-only and out of scope here.
///     </para>
/// </summary>
/// <param name="RowIndex">Picker row index; matches the <c>picker_value_{i}</c> name suffix used by sibling input controls.</param>
/// <param name="Value">Current value to seed the input (single CIDR or comma-separated list); empty when adding a new row.</param>
/// <param name="Placeholder">Placeholder hint shown in the empty input.</param>
public sealed record CidrInputViewModel(
    int RowIndex,
    string Value,
    string Placeholder = "192.168.0.0/16, 10.0.0.0/8");
