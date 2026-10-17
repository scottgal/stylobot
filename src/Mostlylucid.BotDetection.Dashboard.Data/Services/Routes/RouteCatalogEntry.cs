using Mostlylucid.BotDetection.OpenApi;

namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     A unified row for the dashboard Routes tab. Combines:
///     - Discovery data from <see cref="DiscoveredRoute"/> (what the host actually mapped)
///     - Operator-assigned naming from <see cref="RouteNameEntry"/>
///     - OpenAPI documentation from <see cref="IOpenApiCatalog"/> (what the spec advertises)
///
///     An entry can be discovered-only (live but undocumented), documented-only
///     (advertised but not implemented -prime honeypot target), or both
///     (well-described route).
/// </summary>
public sealed record RouteCatalogEntry(
    string RouteKey,
    string Pattern,
    IReadOnlyList<string> HttpMethods,
    string? DisplayName,
    string? FriendlyName,
    string? Notes,
    bool IsDiscovered,
    bool IsDocumented,
    bool RequiresAuthorization,
    bool AllowsAnonymous,
    bool HasOpenApiMetadata,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    RouteCategory Category,
    LoadedOpenApiOperation? OpenApiOperation);
