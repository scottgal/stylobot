using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class EndpointPinEndpoints
{
    public static IEndpointRouteBuilder MapEndpointPinEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/endpoint-pins")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Endpoint Pins");

        group.MapGet("", HandleList).WithName("GetEndpointPins");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<PinnedEndpoint>>, ProblemHttpResult>> HandleList(
        [FromServices] IPinnedEndpointStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Pinned-endpoint store");
        var pins = await store.GetAllAsync(ct);
        return ApiEndpointHelpers.Paginated(pins, pins.Count);
    }
}
