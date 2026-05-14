using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.Test.Services.Routes;

public class RouteDiscoveryServiceTests
{
    [Fact]
    public void Discover_NoDataSources_ReturnsEmpty()
    {
        var svc = new RouteDiscoveryService(Array.Empty<EndpointDataSource>());
        var routes = svc.DiscoverRoutes();
        Assert.Empty(routes);
    }

    [Fact]
    public void Discover_SingleGetEndpoint_ReturnsOneRoute()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/api/v1/test", new[] { "GET" }, "TestEndpoint"));

        var svc = new RouteDiscoveryService(new[] { src });
        var routes = svc.DiscoverRoutes();

        var route = Assert.Single(routes);
        Assert.Equal("/api/v1/test", route.Pattern);
        Assert.Contains("GET", route.HttpMethods);
        Assert.Equal("TestEndpoint", route.DisplayName);
    }

    [Fact]
    public void Discover_RouteWithAuthorize_ReportsAuthRequired()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/admin", new[] { "GET" }, "Admin", new AuthorizeAttribute()));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.True(route.RequiresAuthorization);
        Assert.False(route.AllowsAnonymous);
    }

    [Fact]
    public void Discover_RouteWithAllowAnonymous_ReportsAnonymousAllowed()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/health", new[] { "GET" }, "Health",
                new AuthorizeAttribute(), new AllowAnonymousAttribute()));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.True(route.AllowsAnonymous);
    }

    [Fact]
    public void Discover_MultipleEndpoints_StableRouteKeys()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/a", new[] { "GET" }, "A"),
            BuildRouteEndpoint("/b/{id}", new[] { "POST" }, "B"));

        var svc = new RouteDiscoveryService(new[] { src });
        var routes = svc.DiscoverRoutes().OrderBy(r => r.RouteKey).ToList();

        Assert.Equal(2, routes.Count);
        Assert.Equal("GET:/a", routes[0].RouteKey);
        Assert.Equal("POST:/b/{id}", routes[1].RouteKey);
    }

    [Fact]
    public void Discover_NoExplicitMethods_ReturnsAnyVerbMarker()
    {
        // An endpoint with no HttpMethodMetadata responds to all verbs.
        var src = BuildDataSource(
            BuildRouteEndpoint("/any", httpMethods: null, displayName: "Any"));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.Contains("*", route.HttpMethods);
    }

    [Fact]
    public void Discover_OpenApiDocumentRoute_IsCategorized()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/openapi/v1.json", new[] { "GET" }, "OpenAPI"));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.Equal(RouteCategory.OpenApiDocument, route.Category);
    }

    [Fact]
    public void Discover_HealthRoute_IsCategorized()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/health", new[] { "GET" }, "Health"));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.Equal(RouteCategory.Health, route.Category);
    }

    [Fact]
    public void Discover_SummaryAndDescription_AreCaptured()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/users", new[] { "GET" }, "List",
                new TestSummary("List users"),
                new TestDescription("Returns a paginated list.")));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.Equal("List users", route.Summary);
        Assert.Equal("Returns a paginated list.", route.Description);
        Assert.True(route.HasOpenApiMetadata);
    }

    [Fact]
    public void Discover_TagsAndGroupName_AreMerged()
    {
        var src = BuildDataSource(
            BuildRouteEndpoint("/admin/things", new[] { "GET" }, "AdminThings",
                new TestTags("admin", "ops"),
                new TestGroupName("Operations")));

        var svc = new RouteDiscoveryService(new[] { src });
        var route = Assert.Single(svc.DiscoverRoutes());
        Assert.Contains("admin", route.Tags);
        Assert.Contains("ops", route.Tags);
        Assert.Contains("Operations", route.Tags);
    }

    [Fact]
    public void Discover_NonRouteEndpoint_IsSkipped()
    {
        // A plain Endpoint (not a RouteEndpoint) should be ignored.
        var endpoint = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "Plain");
        var src = BuildDataSource(endpoint);

        var svc = new RouteDiscoveryService(new[] { src });
        Assert.Empty(svc.DiscoverRoutes());
    }

    private static RouteEndpoint BuildRouteEndpoint(
        string pattern,
        IReadOnlyList<string>? httpMethods,
        string displayName,
        params object[] extraMetadata)
    {
        var meta = new List<object>(extraMetadata);
        if (httpMethods != null)
            meta.Add(new HttpMethodMetadata(httpMethods));
        return new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(meta),
            displayName);
    }

    private static EndpointDataSource BuildDataSource(params Endpoint[] endpoints)
        => new TestEndpointDataSource(endpoints);

    private sealed class TestEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints { get; } = endpoints;
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }

    private sealed record TestSummary(string Summary) : IEndpointSummaryMetadata;
    private sealed record TestDescription(string Description) : IEndpointDescriptionMetadata;
    private sealed record TestGroupName(string EndpointGroupName) : IEndpointGroupNameMetadata;
    private sealed class TestTags(params string[] tags) : ITagsMetadata
    {
        public IReadOnlyList<string> Tags { get; } = tags;
    }
}
