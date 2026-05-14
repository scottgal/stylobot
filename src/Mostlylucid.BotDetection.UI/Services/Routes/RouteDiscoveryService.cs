using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Mostlylucid.BotDetection.UI.Services.Routes;

/// <summary>
///     Walks every registered <see cref="EndpointDataSource"/> and returns the routes the host
///     has mapped. Run on demand (cheap reflection over already-loaded metadata); not cached
///     because endpoint data sources can change at runtime (e.g. dynamic route registration).
/// </summary>
public sealed class RouteDiscoveryService(IEnumerable<EndpointDataSource> dataSources) : IRouteDiscoveryService
{
    private readonly IReadOnlyList<EndpointDataSource> _dataSources = dataSources.ToList();

    public IReadOnlyList<DiscoveredRoute> DiscoverRoutes()
    {
        var results = new List<DiscoveredRoute>();
        foreach (var ds in _dataSources)
        foreach (var endpoint in ds.Endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;

            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                          ?? new[] { "*" };

            var authMeta = route.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var allowAnon = route.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            var summary       = route.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary;
            var description   = route.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description;
            var groupName     = route.Metadata.GetMetadata<IEndpointGroupNameMetadata>()?.EndpointGroupName;
            var tagsFromMeta  = route.Metadata.GetMetadata<ITagsMetadata>()?.Tags;

            var tags = new List<string>();
            if (tagsFromMeta != null) tags.AddRange(tagsFromMeta);
            if (!string.IsNullOrEmpty(groupName) && !tags.Contains(groupName)) tags.Add(groupName);

            // OpenAPI metadata in .NET 8+ surfaces as summary/description metadata OR a type whose
            // namespace contains "OpenApi". Either signals that the route is intentionally documented.
            var hasOpenApi = summary != null || description != null || tagsFromMeta != null
                             || route.Metadata.Any(m => m?.GetType().Namespace?.Contains("OpenApi", StringComparison.OrdinalIgnoreCase) == true);

            var pattern  = route.RoutePattern.RawText ?? string.Empty;
            var category = ClassifyRoute(pattern, route);

            results.Add(new DiscoveredRoute
            {
                Pattern = pattern,
                HttpMethods = methods.ToList(),
                DisplayName = route.DisplayName,
                RequiresAuthorization = authMeta.Count > 0 && !allowAnon,
                AllowsAnonymous = allowAnon,
                HasOpenApiMetadata = hasOpenApi,
                Summary = summary,
                Description = description,
                Tags = tags,
                Category = category
            });
        }
        return results;
    }

    private static RouteCategory ClassifyRoute(string pattern, RouteEndpoint route)
    {
        var lower = pattern.ToLowerInvariant();

        // OpenAPI document endpoints (.NET 10 default route + classic swagger paths)
        if (lower.StartsWith("/openapi/") || lower.Contains("/swagger/") || lower.EndsWith("/openapi.json")
            || lower.EndsWith("/swagger.json"))
            return RouteCategory.OpenApiDocument;

        // Health probes: pattern OR the MapHealthChecks-injected display name
        if (lower == "/health" || lower == "/healthz" || lower.StartsWith("/health/")
            || route.DisplayName?.Contains("HealthCheck", StringComparison.OrdinalIgnoreCase) == true)
            return RouteCategory.Health;

        // SignalR hub endpoints carry HubMetadata
        if (route.Metadata.Any(m => m?.GetType().Name == "HubMetadata"))
            return RouteCategory.SignalRHub;

        // ASP.NET Identity API endpoints
        if (lower.Contains("/register") || lower.Contains("/login") || lower.Contains("/refresh")
            || lower.Contains("/confirmEmail") || lower.Contains("/resetPassword"))
        {
            if (route.Metadata.Any(m => m?.GetType().Namespace?.Contains("Identity", StringComparison.OrdinalIgnoreCase) == true))
                return RouteCategory.Identity;
        }

        return RouteCategory.Standard;
    }
}
