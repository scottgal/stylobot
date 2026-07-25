using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class LabelEndpoints
{
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/labels")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Labels")
            .WithApiBotPolicy();

        group.MapGet("", HandleList).WithName("GetLabels");
        group.MapGet("/counts", HandleCounts).WithName("GetLabelCounts");
        group.MapGet("/{signature}", HandleGetLatest).WithName("GetLabelLatest");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<SignatureLabel>>, ServiceUnavailableHttpResult>> HandleList(
        [FromServices] ISignatureLabelStore? store,
        DateTime? since = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Label store");
        var labels = await store.ListSinceAsync(since, Math.Min(limit, 500), ct);
        return ApiEndpointHelpers.Paginated(labels, limit);
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyDictionary<SignatureLabelKind, int>>>, ServiceUnavailableHttpResult>> HandleCounts(
        [FromServices] ISignatureLabelStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Label store");
        return ApiEndpointHelpers.Single(await store.GetCountsAsync(ct));
    }

    private static async Task<Results<Ok<SingleResponse<SignatureLabel>>, NotFound, ServiceUnavailableHttpResult>> HandleGetLatest(
        string signature,
        [FromServices] ISignatureLabelStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Label store");
        var label = await store.GetLatestAsync(signature, ct);
        if (label is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(label);
    }
}
