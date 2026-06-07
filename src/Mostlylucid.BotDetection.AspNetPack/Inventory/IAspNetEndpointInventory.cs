namespace Mostlylucid.BotDetection.AspNetPack.Inventory;

/// <summary>
///     Read-only descriptor for one ASP.NET endpoint exposed by the host
///     process. Returned by <see cref="IAspNetEndpointInventory" />. Held
///     in-process only -- never serialized over the wire (see the
///     "Defaults (locked-in for v1)" section of the per-SKU images spec).
/// </summary>
/// <param name="Method">First declared HTTP verb (e.g. "GET", "POST"). Defaults to "GET" when no <see cref="Microsoft.AspNetCore.Routing.HttpMethodMetadata" /> is present.</param>
/// <param name="RoutePattern">Raw route pattern text, e.g. <c>/api/v1/users/{id}</c>.</param>
/// <param name="DisplayName">Endpoint display name supplied by the routing infrastructure.</param>
/// <param name="Controller">Controller name when MVC; empty string for minimal APIs.</param>
/// <param name="Action">Action name when MVC; empty string for minimal APIs.</param>
/// <param name="Authorize">Authorize policies / roles applied to the endpoint. Empty list means anonymous.</param>
public sealed record AspNetEndpointDescriptor(
    string Method,
    string RoutePattern,
    string DisplayName,
    string Controller,
    string Action,
    IReadOnlyList<string> Authorize);

/// <summary>
///     Read-only enumeration of the host process's ASP.NET endpoints.
///     Backed by <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource" />
///     at runtime. In-process only -- no cross-process surface (spec lock-in:
///     "<c>IAspNetEndpointInventory</c> is in-process only"). When a remote
///     viewer eventually wants this data, add a deliberate REST surface at
///     that point, gated behind the <c>dashboard-write</c> policy.
/// </summary>
public interface IAspNetEndpointInventory
{
    /// <summary>
    ///     Enumerate every <see cref="Microsoft.AspNetCore.Routing.RouteEndpoint" />
    ///     known to the host. Non-route endpoints (e.g. plain
    ///     <see cref="Microsoft.AspNetCore.Http.Endpoint" />) are skipped.
    /// </summary>
    IReadOnlyList<AspNetEndpointDescriptor> All();
}