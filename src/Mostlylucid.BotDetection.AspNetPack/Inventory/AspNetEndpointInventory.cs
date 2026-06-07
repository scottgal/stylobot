using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;

namespace Mostlylucid.BotDetection.AspNetPack.Inventory;

/// <summary>
///     <see cref="IAspNetEndpointInventory" /> implementation that snapshots
///     the host's <see cref="EndpointDataSource" /> on every call. Cheap to
///     invoke: endpoint enumeration is O(N) over already-materialized routes
///     and the dashboard surface only renders a couple of times per minute.
/// </summary>
public sealed class AspNetEndpointInventory : IAspNetEndpointInventory
{
    private readonly EndpointDataSource _ds;

    public AspNetEndpointInventory(EndpointDataSource ds) => _ds = ds;

    public IReadOnlyList<AspNetEndpointDescriptor> All()
    {
        var list = new List<AspNetEndpointDescriptor>();
        foreach (var ep in _ds.Endpoints)
        {
            if (ep is not RouteEndpoint route) continue;

            var verb = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var pattern = route.RoutePattern.RawText ?? "";
            var name = route.DisplayName ?? "";

            var cad = route.Metadata.GetMetadata<ControllerActionDescriptor>();
            var controller = cad?.ControllerName ?? "";
            var action = cad?.ActionName ?? "";

            var authorize = route.Metadata
                .OfType<IAuthorizeData>()
                .Select(a => a.Policy ?? a.Roles ?? "any-auth")
                .ToList();

            list.Add(new AspNetEndpointDescriptor(verb, pattern, name, controller, action, authorize));
        }

        return list;
    }
}