namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     A single route discovered from an <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource"/>.
///     The <see cref="RouteKey"/> ("METHOD:/pattern") is the stable identifier used by the manual-name
///     store and the dashboard.
/// </summary>
public sealed record DiscoveredRoute
{
    public required string Pattern { get; init; }
    public required IReadOnlyList<string> HttpMethods { get; init; }
    public string? DisplayName { get; init; }
    public bool RequiresAuthorization { get; init; }
    public bool AllowsAnonymous { get; init; }
    public bool HasOpenApiMetadata { get; init; }

    /// <summary>OpenAPI summary text, when supplied via <c>WithSummary()</c> or <c>[EndpointSummary]</c>.</summary>
    public string? Summary { get; init; }

    /// <summary>OpenAPI description text, when supplied via <c>WithDescription()</c> or <c>[EndpointDescription]</c>.</summary>
    public string? Description { get; init; }

    /// <summary>OpenAPI tags / endpoint group name, when supplied via <c>WithTags()</c> or <c>WithGroupName()</c>.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>
    ///     Heuristic category. Helps the dashboard segment routes (e.g. "show me OpenAPI doc
    ///     endpoints" or "hide health probes"). Computed from pattern + metadata.
    /// </summary>
    public RouteCategory Category { get; init; } = RouteCategory.Standard;

    /// <summary>
    ///     Stable identifier for this route across restarts. Format is "METHOD:/pattern" for
    ///     the first listed method, or "*:/pattern" when no explicit method metadata is present.
    /// </summary>
    public string RouteKey => $"{HttpMethods.FirstOrDefault() ?? "*"}:{Pattern}";
}

public enum RouteCategory
{
    Standard,
    OpenApiDocument,    // e.g. /openapi/v1.json, /swagger/v1/swagger.json
    Health,             // routes registered via MapHealthChecks
    StaticAsset,        // wwwroot / static files
    SignalRHub,         // /_stylobot etc.
    Identity            // ASP.NET Identity API endpoints
}
