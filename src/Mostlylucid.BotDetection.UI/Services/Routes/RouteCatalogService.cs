namespace Mostlylucid.BotDetection.UI.Services.Routes;

public sealed class RouteCatalogService(
    IRouteDiscoveryService discovery,
    IRouteNameStore nameStore)
    : IRouteCatalogService
{
    public async Task<IReadOnlyList<RouteCatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        var discovered = discovery.DiscoverRoutes();
        var names = await nameStore.GetAllAsync(ct);
        var nameLookup = names.ToDictionary(n => n.RouteKey, n => n);

        var entries = new List<RouteCatalogEntry>(discovered.Count);
        foreach (var r in discovered)
        {
            nameLookup.TryGetValue(r.RouteKey, out var name);
            entries.Add(new RouteCatalogEntry(
                RouteKey: r.RouteKey,
                Pattern: r.Pattern,
                HttpMethods: r.HttpMethods,
                DisplayName: r.DisplayName,
                FriendlyName: name?.FriendlyName,
                Notes: name?.Notes,
                RequiresAuthorization: r.RequiresAuthorization,
                AllowsAnonymous: r.AllowsAnonymous,
                HasOpenApiMetadata: r.HasOpenApiMetadata,
                Summary: r.Summary,
                Description: r.Description,
                Tags: r.Tags,
                Category: r.Category));
        }
        return entries.OrderBy(e => e.RouteKey, StringComparer.Ordinal).ToList();
    }
}
