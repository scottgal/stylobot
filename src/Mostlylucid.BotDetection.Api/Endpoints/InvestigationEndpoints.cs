using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Unified investigation surface: filter detections by entity (signature, country,
///     path, ua_family, ip, fingerprint) and get a cross-associated rollup. Backs the
///     dashboard's Investigate tab in remote mode. Shape-search and presets are
///     optional commercial surfaces - the endpoints 503 cleanly when the
///     <see cref="IShapeSearchStore"/> isn't registered.
/// </summary>
public static class InvestigationEndpoints
{
    public static IEndpointRouteBuilder MapInvestigationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/investigate")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Investigation");

        group.MapPost("", HandleInvestigate).WithName("Investigate").WithOpenApi();
        group.MapPost("/shape-search", HandleShapeSearch).WithName("InvestigateShapeSearch").WithOpenApi();
        group.MapGet("/presets", HandlePresets).WithName("GetInvestigationPresets").WithOpenApi();

        return endpoints;
    }

    private static async Task<Ok<SingleResponse<InvestigationResult>>> HandleInvestigate(
        InvestigationFilter filter,
        [FromServices] IDashboardEventStore store,
        CancellationToken ct = default)
    {
        var result = await store.GetInvestigationAsync(filter, ct);
        return TypedResults.Ok(new SingleResponse<InvestigationResult>
        {
            Data = result,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<InvestigationResult>>, ProblemHttpResult>> HandleShapeSearch(
        ShapeSearchFilter filter,
        [FromServices] IShapeSearchStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Shape search not enabled.", statusCode: 503);

        var result = await store.SearchByShapeAsync(filter, ct);
        return TypedResults.Ok(new SingleResponse<InvestigationResult>
        {
            Data = result,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<PaginatedResponse<InvestigationPreset>>, ProblemHttpResult>> HandlePresets(
        [FromServices] IShapeSearchStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Shape search not enabled.", statusCode: 503);

        var presets = await store.GetPresetsAsync(ct);
        return TypedResults.Ok(new PaginatedResponse<InvestigationPreset>
        {
            Data = presets,
            Pagination = new PaginationInfo { Offset = 0, Limit = presets.Count, Total = presets.Count },
            Meta = new ResponseMeta()
        });
    }
}
