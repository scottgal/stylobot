namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     Catalog entry for a single instrument the gateway publishes, as observed
///     by <see cref="IMeterStream" />. Returned by <see cref="IMeterStream.ListAsync" />
///     so callers can enumerate available series before requesting a windowed slice.
/// </summary>
/// <param name="Name">
///     The full instrument name (e.g. <c>stylobot_aspnet_observations_received_total</c>).
/// </param>
/// <param name="Namespace">
///     The first underscore- or dot-separated segment of the name (e.g. <c>stylobot</c>),
///     used by the dashboard to group instruments under a pack heading.
/// </param>
/// <param name="Kind">The instrument kind that drives bucket aggregation.</param>
/// <param name="Unit">The unit the instrument was declared with (e.g. <c>ms</c>, <c>1</c>), or null.</param>
/// <param name="Description">The instrument description, or null when not declared.</param>
public sealed record MeterCatalogEntry(
    string Name,
    string Namespace,
    MeterKind Kind,
    string? Unit,
    string? Description);
