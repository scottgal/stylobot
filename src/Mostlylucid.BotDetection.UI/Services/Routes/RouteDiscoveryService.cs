using Microsoft.AspNetCore.Authorization;
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

            // OpenAPI metadata in .NET 8+ surfaces as IEndpointDescriptionMetadata + IEndpointSummaryMetadata
            // OR the legacy Microsoft.AspNetCore.OpenApi attribute on the endpoint. Either signals that the
            // route is documented.
            var hasOpenApi = route.Metadata.Any(m =>
                m?.GetType().Namespace?.Contains("OpenApi", StringComparison.OrdinalIgnoreCase) == true
                || m is Microsoft.AspNetCore.Http.Metadata.IEndpointSummaryMetadata
                || m is Microsoft.AspNetCore.Http.Metadata.IEndpointDescriptionMetadata);

            results.Add(new DiscoveredRoute
            {
                Pattern = route.RoutePattern.RawText ?? string.Empty,
                HttpMethods = methods.ToList(),
                DisplayName = route.DisplayName,
                RequiresAuthorization = authMeta.Count > 0 && !allowAnon,
                AllowsAnonymous = allowAnon,
                HasOpenApiMetadata = hasOpenApi
            });
        }
        return results;
    }
}
