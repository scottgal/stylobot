using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
}
