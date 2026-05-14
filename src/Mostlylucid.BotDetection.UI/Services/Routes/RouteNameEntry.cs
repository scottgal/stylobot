namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     Operator-assigned friendly name for a discovered route. <see cref="RouteKey"/>
///     matches <see cref="DiscoveredRoute.RouteKey"/>; the rest is editor metadata.
/// </summary>
public sealed record RouteNameEntry(
    string RouteKey,
    string FriendlyName,
    string? Notes,
    DateTime UpdatedUtc,
    string? UpdatedBy);
