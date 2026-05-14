namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     A unified row for the dashboard Routes tab. Combines discovery data
///     (<see cref="DiscoveredRoute"/>) with operator-assigned naming
///     (<see cref="RouteNameEntry"/>). Names are nullable when no override has
///     been set, leaving the dashboard to fall back to <see cref="DisplayName"/>
///     or the raw <see cref="Pattern"/>.
/// </summary>
public sealed record RouteCatalogEntry(
    string RouteKey,
    string Pattern,
    IReadOnlyList<string> HttpMethods,
    string? DisplayName,
    string? FriendlyName,
    string? Notes,
    bool RequiresAuthorization,
    bool AllowsAnonymous,
    bool HasOpenApiMetadata,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    RouteCategory Category);
