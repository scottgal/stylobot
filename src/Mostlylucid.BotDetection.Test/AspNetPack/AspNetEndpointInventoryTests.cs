using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.AspNetPack.Inventory;

namespace Mostlylucid.BotDetection.Test.AspNetPack;

public class AspNetEndpointInventoryTests
{
    [Fact]
    public void All_with_empty_data_source_returns_empty_list()
    {
        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(Array.Empty<Endpoint>()));

        var result = inv.All();

        Assert.Empty(result);
    }

    [Fact]
    public void All_with_two_route_endpoints_returns_two_descriptors_populated()
    {
        var ep1 = BuildRouteEndpoint(
            "/api/v1/widgets",
            "GET",
            "WidgetsController.List",
            controller: "Widgets",
            action: "List");
        var ep2 = BuildRouteEndpoint(
            "/api/v1/widgets/{id}",
            "POST",
            "WidgetsController.Update",
            controller: "Widgets",
            action: "Update");

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new[] { ep1, ep2 }));

        var result = inv.All();

        Assert.Equal(2, result.Count);

        Assert.Equal("GET", result[0].Method);
        Assert.Equal("/api/v1/widgets", result[0].RoutePattern);
        Assert.Equal("WidgetsController.List", result[0].DisplayName);
        Assert.Equal("Widgets", result[0].Controller);
        Assert.Equal("List", result[0].Action);

        Assert.Equal("POST", result[1].Method);
        Assert.Equal("/api/v1/widgets/{id}", result[1].RoutePattern);
        Assert.Equal("Update", result[1].Action);
    }

    [Fact]
    public void All_skips_non_route_endpoints()
    {
        var plain = new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "PlainEndpoint");
        var route = BuildRouteEndpoint("/a", "GET", "A");

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new Endpoint[] { plain, route }));

        var result = inv.All();

        Assert.Single(result);
        Assert.Equal("/a", result[0].RoutePattern);
    }

    [Fact]
    public void All_defaults_to_GET_when_no_HttpMethodMetadata_present()
    {
        // Endpoint registered without HttpMethodMetadata should still surface in the
        // inventory -- we want the operator to see ALL routes -- but the verb column
        // should fall back to "GET" rather than throw or blank out.
        var ep = BuildRouteEndpoint("/any", method: null, "Any");

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new[] { ep }));

        var result = inv.All();

        Assert.Single(result);
        Assert.Equal("GET", result[0].Method);
    }

    [Fact]
    public void All_extracts_authorize_policies_and_roles()
    {
        var ep = BuildRouteEndpoint("/admin", "GET", "Admin",
            extra: new object[]
            {
                new AuthorizeAttribute { Policy = "DashboardWritePolicy" },
                new AuthorizeAttribute { Roles  = "dashboard-write" },
            });

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new[] { ep }));

        var result = inv.All();

        var auth = Assert.Single(result).Authorize;
        Assert.Contains("DashboardWritePolicy", auth);
        Assert.Contains("dashboard-write", auth);
    }

    [Fact]
    public void All_authorize_with_no_policy_or_roles_yields_any_auth_sentinel()
    {
        var ep = BuildRouteEndpoint("/needs-auth", "GET", "NeedsAuth",
            extra: new object[] { new AuthorizeAttribute() });

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new[] { ep }));

        var result = inv.All();
        var auth = Assert.Single(result).Authorize;

        Assert.Single(auth);
        Assert.Equal("any-auth", auth[0]);
    }

    [Fact]
    public void All_anonymous_route_has_empty_authorize_list()
    {
        var ep = BuildRouteEndpoint("/health", "GET", "Health");

        var inv = new AspNetEndpointInventory(new TestEndpointDataSource(new[] { ep }));

        var result = inv.All();
        Assert.Empty(Assert.Single(result).Authorize);
    }

    private static RouteEndpoint BuildRouteEndpoint(
        string pattern,
        string? method,
        string displayName,
        string? controller = null,
        string? action = null,
        object[]? extra = null)
    {
        var meta = new List<object>();
        if (method is not null)
            meta.Add(new HttpMethodMetadata(new[] { method }));
        if (controller is not null || action is not null)
        {
            meta.Add(new ControllerActionDescriptor
            {
                ControllerName = controller ?? "",
                ActionName = action ?? "",
            });
        }
        if (extra is not null) meta.AddRange(extra);

        return new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(meta),
            displayName);
    }

    private sealed class TestEndpointDataSource : EndpointDataSource
    {
        public TestEndpointDataSource(IReadOnlyList<Endpoint> endpoints)
        {
            Endpoints = endpoints;
        }

        public override IReadOnlyList<Endpoint> Endpoints { get; }
        public override IChangeToken GetChangeToken() => new CancellationChangeToken(CancellationToken.None);
    }
}
