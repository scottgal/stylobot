using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only pinned-endpoint surface for remote dashboards. Wraps
///     <see cref="IPinnedEndpointStore"/>. Write paths (Add / Remove) stay local to the
///     gateway that owns the pins database.
/// </summary>
public static class EndpointPinEndpoints
{
    public static IEndpointRouteBuilder MapEndpointPinEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/endpoint-pins")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Endpoint Pins");

        group.MapGet("", HandleList).WithName("GetEndpointPins").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<PinnedEndpoint>>, ProblemHttpResult>> HandleList(
        [FromServices] IPinnedEndpointStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Pinned-endpoint store not enabled.", statusCode: 503);

        var pins = await store.GetAllAsync(ct);
        return TypedResults.Ok(new PaginatedResponse<PinnedEndpoint>
        {
            Data = pins,
            Pagination = new PaginationInfo { Offset = 0, Limit = pins.Count, Total = pins.Count },
            Meta = new ResponseMeta()
        });
    }
}
