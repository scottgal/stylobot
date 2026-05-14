using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.OpenApi;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.Test.Services.Routes;

public class RouteCatalogServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteRouteNameStore _nameStore;
    private readonly OpenApiCatalog _openApi = new();

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
        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);

        var entries = await svc.GetCatalogAsync();
        var entry = Assert.Single(entries);
        Assert.Null(entry.FriendlyName);
        Assert.Equal("GET:/a", entry.RouteKey);
        Assert.True(entry.IsDiscovered);
        Assert.False(entry.IsDocumented);
    }

    [Fact]
    public async Task Catalog_AssignedName_IsMergedIn()
    {
        var discovery = new RouteDiscoveryService(new[] { Source("/users", "GET", "ListUsers") });
        await _nameStore.SetAsync("GET:/users", "List Users", "paginated", "admin");

        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
        var entry = Assert.Single(await svc.GetCatalogAsync());

        Assert.Equal("List Users", entry.FriendlyName);
        Assert.Equal("paginated", entry.Notes);
    }

    [Fact]
    public async Task Catalog_OrphanName_IsNotReturned()
    {
        var discovery = new RouteDiscoveryService(Array.Empty<EndpointDataSource>());
        await _nameStore.SetAsync("GET:/gone", "Gone", null, "admin");

        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
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
        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
        var keys = (await svc.GetCatalogAsync()).Select(e => e.RouteKey).ToList();

        Assert.Equal(new[] { "GET:/m", "GET:/z", "POST:/a" }, keys);
    }

    [Fact]
    public async Task Catalog_DocumentedAndDiscovered_FlagsBoth()
    {
        var discovery = new RouteDiscoveryService(new[] { Source("/users", "GET", "ListUsers") });
        _openApi.Replace(new[]
        {
            new LoadedOpenApiDocument("test", "T", "1.0",
                new[]
                {
                    new LoadedOpenApiOperation("/users", "get", "ListUsers",
                        "List users", "Returns paginated list", Deprecated: false,
                        Tags: new[] { "users" }, ResponseStatusCodes: new[] { 200 },
                        SecuritySchemes: Array.Empty<string>())
                },
                DateTime.UtcNow, "{}")
        });

        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
        var entry = Assert.Single(await svc.GetCatalogAsync());

        Assert.True(entry.IsDiscovered);
        Assert.True(entry.IsDocumented);
        Assert.Equal("List users", entry.Summary);
        Assert.Contains("users", entry.Tags);
        Assert.NotNull(entry.OpenApiOperation);
    }

    [Fact]
    public async Task Catalog_DocumentedNotDiscovered_AppearsAsPhantom()
    {
        var discovery = new RouteDiscoveryService(Array.Empty<EndpointDataSource>());
        _openApi.Replace(new[]
        {
            new LoadedOpenApiDocument("test", "T", "1.0",
                new[]
                {
                    new LoadedOpenApiOperation("/missing", "post", "Missing",
                        "Phantom route", null, Deprecated: false,
                        Tags: Array.Empty<string>(), ResponseStatusCodes: new[] { 200 },
                        SecuritySchemes: Array.Empty<string>())
                },
                DateTime.UtcNow, "{}")
        });

        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
        var entry = Assert.Single(await svc.GetCatalogAsync());

        Assert.False(entry.IsDiscovered);
        Assert.True(entry.IsDocumented);
        Assert.Equal("POST:/missing", entry.RouteKey);
        Assert.Equal("Phantom route", entry.Summary);
    }

    [Fact]
    public async Task Catalog_SecuritySchemesOnDocumentedOp_ReportsAuthRequired()
    {
        var discovery = new RouteDiscoveryService(Array.Empty<EndpointDataSource>());
        _openApi.Replace(new[]
        {
            new LoadedOpenApiDocument("test", "T", "1.0",
                new[]
                {
                    new LoadedOpenApiOperation("/admin", "get", "Admin",
                        null, null, Deprecated: false,
                        Tags: Array.Empty<string>(), ResponseStatusCodes: new[] { 200 },
                        SecuritySchemes: new[] { "bearerAuth" })
                },
                DateTime.UtcNow, "{}")
        });

        var svc = new RouteCatalogService(discovery, _nameStore, _openApi);
        var entry = Assert.Single(await svc.GetCatalogAsync());

        Assert.True(entry.RequiresAuthorization);
        Assert.False(entry.AllowsAnonymous);
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
