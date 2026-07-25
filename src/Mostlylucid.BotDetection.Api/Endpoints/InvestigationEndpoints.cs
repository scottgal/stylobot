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

public static class InvestigationEndpoints
{
    public static IEndpointRouteBuilder MapInvestigationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/investigate")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Investigation")
            .WithApiBotPolicy();

        group.MapPost("", HandleInvestigate).WithName("Investigate");
        group.MapPost("/shape-search", HandleShapeSearch).WithName("InvestigateShapeSearch");
        group.MapGet("/presets", HandlePresets).WithName("GetInvestigationPresets");

        return endpoints;
    }

    private static async Task<Ok<SingleResponse<InvestigationResult>>> HandleInvestigate(
        InvestigationFilter filter,
        [FromServices] IDashboardEventStore store,
        CancellationToken ct = default)
        => ApiEndpointHelpers.Single(await store.GetInvestigationAsync(filter, ct));

    private static async Task<Results<Ok<SingleResponse<InvestigationResult>>, ServiceUnavailableHttpResult>> HandleShapeSearch(
        ShapeSearchFilter filter,
        [FromServices] IShapeSearchStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Shape search");
        return ApiEndpointHelpers.Single(await store.SearchByShapeAsync(filter, ct));
    }

    private static async Task<Results<Ok<PaginatedResponse<InvestigationPreset>>, ServiceUnavailableHttpResult>> HandlePresets(
        [FromServices] IShapeSearchStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Shape search");
        var presets = await store.GetPresetsAsync(ct);
        return ApiEndpointHelpers.Paginated(presets, presets.Count);
    }
}
