using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only fingerprint identity surface for remote dashboards. Wraps
///     <see cref="IFingerprintReader"/>. Backs the dashboard's Identities tab and the
///     per-fingerprint drill-in. Write paths (centroid updates, score caching, observation
///     absorption) stay local to whoever owns the identity database.
/// </summary>
public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/fingerprints")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Identities");

        group.MapGet("", HandleList).WithName("GetFingerprints").WithOpenApi();
        group.MapGet("/{fingerprintId}", HandleGet).WithName("GetFingerprint").WithOpenApi();
        group.MapGet("/unabsorbed-counts", HandleUnabsorbedCounts).WithName("GetFingerprintUnabsorbedCounts").WithOpenApi();
        group.MapGet("/{fingerprintId}/unabsorbed-count", HandleUnabsorbedCount).WithName("GetFingerprintUnabsorbedCount").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<Fingerprint>>, ProblemHttpResult>> HandleList(
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Identity layer not enabled.", statusCode: 503);

        var fingerprints = await store.ListFingerprintsAsync(ct);
        return TypedResults.Ok(new PaginatedResponse<Fingerprint>
        {
            Data = fingerprints,
            Pagination = new PaginationInfo { Offset = 0, Limit = fingerprints.Count, Total = fingerprints.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<Fingerprint>>, NotFound, ProblemHttpResult>> HandleGet(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Identity layer not enabled.", statusCode: 503);

        var fp = await store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<Fingerprint>
        {
            Data = fp,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyDictionary<string, int>>>, ProblemHttpResult>> HandleUnabsorbedCounts(
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Identity layer not enabled.", statusCode: 503);

        var counts = await store.GetUnabsorbedObservationCountsAsync(ct);
        return TypedResults.Ok(new SingleResponse<IReadOnlyDictionary<string, int>>
        {
            Data = counts,
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<int>>, ProblemHttpResult>> HandleUnabsorbedCount(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Identity layer not enabled.", statusCode: 503);

        var count = await store.GetUnabsorbedObservationCountAsync(fingerprintId, ct);
        return TypedResults.Ok(new SingleResponse<int>
        {
            Data = count,
            Meta = new ResponseMeta()
        });
    }
}
