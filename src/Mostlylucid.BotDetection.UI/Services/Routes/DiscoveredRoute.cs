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

    /// <summary>
    ///     Stable identifier for this route across restarts. Format is "METHOD:/pattern" for
    ///     the first listed method, or "*:/pattern" when no explicit method metadata is present.
    /// </summary>
    public string RouteKey => $"{HttpMethods.FirstOrDefault() ?? "*"}:{Pattern}";
}
