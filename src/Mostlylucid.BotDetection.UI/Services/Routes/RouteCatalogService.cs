using Mostlylucid.BotDetection.OpenApi;

namespace Mostlylucid.BotDetection.UI.Services.Routes;

public sealed class RouteCatalogService(
    IRouteDiscoveryService discovery,
    IRouteNameStore nameStore,
    IOpenApiCatalog openApiCatalog)
    : IRouteCatalogService
{
    public async Task<IReadOnlyList<RouteCatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        var discovered = discovery.DiscoverRoutes();
        var names = await nameStore.GetAllAsync(ct);
        var nameLookup = names.ToDictionary(n => n.RouteKey, n => n);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<RouteCatalogEntry>(discovered.Count);

        foreach (var r in discovered)
        {
            nameLookup.TryGetValue(r.RouteKey, out var name);
            var op = openApiCatalog.TryGetByRouteKey(r.RouteKey);
            seen.Add(r.RouteKey);

            entries.Add(new RouteCatalogEntry(
                RouteKey: r.RouteKey,
                Pattern: r.Pattern,
                HttpMethods: r.HttpMethods,
                DisplayName: r.DisplayName,
                FriendlyName: name?.FriendlyName,
                Notes: name?.Notes,
                IsDiscovered: true,
                IsDocumented: op != null,
                RequiresAuthorization: r.RequiresAuthorization,
                AllowsAnonymous: r.AllowsAnonymous,
                HasOpenApiMetadata: r.HasOpenApiMetadata,
                Summary: op?.Summary ?? r.Summary,
                Description: op?.Description ?? r.Description,
                Tags: op?.Tags.Count > 0 ? op.Tags : r.Tags,
                Category: r.Category,
                OpenApiOperation: op));
        }

        // Surface documented-but-not-discovered operations. They're useful honeypot
        // targets and reveal drift between the spec and the running app.
        foreach (var op in openApiCatalog.AllOperations())
        {
            var routeKey = $"{op.Method.ToUpperInvariant()}:{op.Path}";
            if (!seen.Add(routeKey)) continue;
            nameLookup.TryGetValue(routeKey, out var name);

            entries.Add(new RouteCatalogEntry(
                RouteKey: routeKey,
                Pattern: op.Path,
                HttpMethods: new[] { op.Method.ToUpperInvariant() },
                DisplayName: op.OperationId,
                FriendlyName: name?.FriendlyName,
                Notes: name?.Notes,
                IsDiscovered: false,
                IsDocumented: true,
                RequiresAuthorization: op.SecuritySchemes.Count > 0,
                AllowsAnonymous: op.SecuritySchemes.Count == 0,
                HasOpenApiMetadata: true,
                Summary: op.Summary,
                Description: op.Description,
                Tags: op.Tags,
                Category: RouteCategory.Standard,
                OpenApiOperation: op));
        }

        return entries.OrderBy(e => e.RouteKey, StringComparer.Ordinal).ToList();
    }
}
