using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/fingerprints")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Identities");

        group.MapGet("", HandleList).WithName("GetFingerprints");
        group.MapGet("/{fingerprintId}", HandleGet).WithName("GetFingerprint");
        group.MapGet("/unabsorbed-counts", HandleUnabsorbedCounts).WithName("GetFingerprintUnabsorbedCounts");
        group.MapGet("/{fingerprintId}/unabsorbed-count", HandleUnabsorbedCount).WithName("GetFingerprintUnabsorbedCount");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<Fingerprint>>, ProblemHttpResult>> HandleList(
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var fingerprints = await store.ListFingerprintsAsync(ct);
        return ApiEndpointHelpers.Paginated(fingerprints, fingerprints.Count);
    }

    private static async Task<Results<Ok<SingleResponse<Fingerprint>>, NotFound, ProblemHttpResult>> HandleGet(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var fp = await store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(fp);
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyDictionary<string, int>>>, ProblemHttpResult>> HandleUnabsorbedCounts(
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        return ApiEndpointHelpers.Single(await store.GetUnabsorbedObservationCountsAsync(ct));
    }

    private static async Task<Results<Ok<SingleResponse<int>>, ProblemHttpResult>> HandleUnabsorbedCount(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        return ApiEndpointHelpers.Single(await store.GetUnabsorbedObservationCountAsync(fingerprintId, ct));
    }
}
