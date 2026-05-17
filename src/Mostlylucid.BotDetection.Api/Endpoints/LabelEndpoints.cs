using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only signature-label surface for remote dashboards. Wraps
///     <see cref="ISignatureLabelStore"/>. Writes (UpsertAsync / RemoveAsync) stay local
///     to whoever owns the labels database; the remote viewer is read-only.
/// </summary>
public static class LabelEndpoints
{
    public static IEndpointRouteBuilder MapLabelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/labels")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Labels");

        group.MapGet("", HandleList).WithName("GetLabels").WithOpenApi();
        group.MapGet("/counts", HandleCounts).WithName("GetLabelCounts").WithOpenApi();
        group.MapGet("/{signature}", HandleGetLatest).WithName("GetLabelLatest").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<SignatureLabel>>, ProblemHttpResult>> HandleList(
        [FromServices] ISignatureLabelStore? store,
        DateTime? since = null,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Label store not enabled.", statusCode: 503);

        var labels = await store.ListSinceAsync(since, Math.Min(limit, 500), ct);
        return TypedResults.Ok(new PaginatedResponse<SignatureLabel>
        {
            Data = labels,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = labels.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyDictionary<SignatureLabelKind, int>>>, ProblemHttpResult>> HandleCounts(
        [FromServices] ISignatureLabelStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Label store not enabled.", statusCode: 503);

        var counts = await store.GetCountsAsync(ct);
        return TypedResults.Ok(new SingleResponse<IReadOnlyDictionary<SignatureLabelKind, int>>
        {
            Data = counts,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<SignatureLabel>>, NotFound, ProblemHttpResult>> HandleGetLatest(
        string signature,
        [FromServices] ISignatureLabelStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Label store not enabled.", statusCode: 503);

        var label = await store.GetLatestAsync(signature, ct);
        if (label is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<SignatureLabel>
        {
            Data = label,
            Meta = new ResponseMeta()
        });
    }
}
