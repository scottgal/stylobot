using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.Test.Services.Routes;

public class RouteCatalogServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteRouteNameStore _nameStore;

    public RouteCatalogServiceTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _nameStore = new SqliteRouteNameStore(_conn, NullLogger<SqliteRouteNameStore>.Instance);
        _nameStore.InitializeAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Catalog_NoNamesAssigned_LeavesFriendlyNameNull()
    {
        var discovery = new RouteDiscoveryService(new[] { Source("/a", "GET", "A") });
        var svc = new RouteCatalogService(discovery, _nameStore);

        var entries = await svc.GetCatalogAsync();
        var entry = Assert.Single(entries);
        Assert.Null(entry.FriendlyName);
        Assert.Equal("GET:/a", entry.RouteKey);
    }

    [Fact]
    public async Task Catalog_AssignedName_IsMergedIn()
    {
        var discovery = new RouteDiscoveryService(new[] { Source("/users", "GET", "ListUsers") });
        await _nameStore.SetAsync("GET:/users", "List Users", "paginated", "admin");

        var svc = new RouteCatalogService(discovery, _nameStore);
        var entry = Assert.Single(await svc.GetCatalogAsync());

        Assert.Equal("List Users", entry.FriendlyName);
        Assert.Equal("paginated", entry.Notes);
    }

    [Fact]
    public async Task Catalog_OrphanName_IsNotReturned()
    {
        // Name exists in store but no matching route is discovered (route was removed).
        var discovery = new RouteDiscoveryService(Array.Empty<EndpointDataSource>());
        await _nameStore.SetAsync("GET:/gone", "Gone", null, "admin");

        var svc = new RouteCatalogService(discovery, _nameStore);
        Assert.Empty(await svc.GetCatalogAsync());
    }

    [Fact]
    public async Task Catalog_SortedByRouteKey()
    {
        var discovery = new RouteDiscoveryService(new[]
        {
            Source("/z", "GET", "Z"),
            Source("/a", "POST", "A"),
            Source("/m", "GET", "M")
        });
        var svc = new RouteCatalogService(discovery, _nameStore);
        var keys = (await svc.GetCatalogAsync()).Select(e => e.RouteKey).ToList();

        Assert.Equal(new[] { "GET:/m", "GET:/z", "POST:/a" }, keys);
    }

    private static EndpointDataSource Source(string pattern, string method, string displayName)
        => new TestSource(new[]
        {
            (Endpoint)new RouteEndpoint(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse(pattern),
                order: 0,
                new EndpointMetadataCollection(new HttpMethodMetadata(new[] { method })),
                displayName)
        });

    private sealed class TestSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    public async ValueTask DisposeAsync() => await _conn.DisposeAsync();
}
