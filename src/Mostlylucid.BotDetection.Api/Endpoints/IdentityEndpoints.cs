using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/fingerprints")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Identities")
            .WithApiBotPolicy();

        group.MapGet("", HandleList).WithName("GetFingerprints");
        group.MapGet("/lookup/{primarySignature}", HandleLookup).WithName("LookupFingerprintId");
        group.MapGet("/lookup-by-id/{fingerprintId}", HandleLookupByFingerprintId).WithName("LookupSignatureForFingerprint");
        group.MapGet("/nearest/{primarySignature}", HandleNearest).WithName("GetFingerprintNearest");
        group.MapGet("/unabsorbed-counts", HandleUnabsorbedCounts).WithName("GetFingerprintUnabsorbedCounts");
        group.MapGet("/{fingerprintId}", HandleGet).WithName("GetFingerprint");
        group.MapGet("/{fingerprintId}/unabsorbed-count", HandleUnabsorbedCount).WithName("GetFingerprintUnabsorbedCount");
        group.MapGet("/{fingerprintId}/browser-modes", HandleBrowserModes).WithName("GetFingerprintBrowserModes");

        return endpoints;
    }

    /// <summary>
    ///     Per-fingerprint browser-mode rows. Returns the persisted
    ///     <c>fingerprint_modes</c> entries for one fingerprint (same browser,
    ///     different modes — composite spec step 7 + 8). Surfaces the
    ///     gateway-local store over REST so a remote-mode dashboard host can
    ///     render the Modes panel without direct DB access. Returns
    ///     <see cref="StatusCodes.Status503ServiceUnavailable"/> when the store
    ///     isn't registered (identity disabled, or this gateway doesn't run
    ///     persistence), and an empty list when the fingerprint exists but has
    ///     no mode rows yet.
    /// </summary>
    private static async Task<Results<Ok<PaginatedResponse<FingerprintBrowserMode>>, ServiceUnavailableHttpResult>> HandleBrowserModes(
        string fingerprintId,
        [FromServices] IFingerprintBrowserModeStore? modes,
        CancellationToken ct = default)
    {
        if (modes is null) return ApiEndpointHelpers.StoreUnavailable("Browser-mode store");
        var rows = await modes.GetModesAsync(fingerprintId, ct);
        return ApiEndpointHelpers.Paginated(rows, rows.Count);
    }

    private static async Task<Results<Ok<PaginatedResponse<Fingerprint>>, ServiceUnavailableHttpResult>> HandleList(
        [FromServices] IFingerprintReader? store,
        [FromServices] IOptions<BotDetectionOptions> opts,
        [FromQuery] int? offset = null,
        [FromQuery] int? limit = null,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var cap = opts.Value.Identity?.Engine?.MaxFingerprintsPerPage ?? 200;
        var effectiveOffset = offset ?? 0;
        var effectiveLimit = Math.Clamp(limit ?? cap, 1, cap);
        var fingerprints = await store.ListFingerprintsAsync(effectiveOffset, effectiveLimit, ct);
        return ApiEndpointHelpers.Paginated(fingerprints, effectiveOffset, effectiveLimit);
    }

    private static async Task<Results<Ok<SingleResponse<Fingerprint>>, NotFound, ServiceUnavailableHttpResult>> HandleGet(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var fp = await store.GetFingerprintAsync(fingerprintId, ct);
        if (fp is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(fp);
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyDictionary<string, int>>>, ServiceUnavailableHttpResult>> HandleUnabsorbedCounts(
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        return ApiEndpointHelpers.Single(await store.GetUnabsorbedObservationCountsAsync(ct));
    }

    private static async Task<Results<Ok<SingleResponse<int>>, ServiceUnavailableHttpResult>> HandleUnabsorbedCount(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        return ApiEndpointHelpers.Single(await store.GetUnabsorbedObservationCountAsync(fingerprintId, ct));
    }

    private static async Task<Results<Ok<SingleResponse<string>>, NotFound, ServiceUnavailableHttpResult>> HandleLookup(
        string primarySignature,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var fpId = await store.LookupFingerprintIdAsync(primarySignature, ct);
        if (string.IsNullOrEmpty(fpId)) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(fpId);
    }

    /// <summary>
    ///     Reverse binding lookup (2026-08-16, the signature-detail 404 gate-red): the
    ///     primary signature bound to a fingerprint id — lets a remote-mode dashboard
    ///     host resolve the Identities / looks-like surfaces' fingerprint ids to their
    ///     signatures instead of 404ing the detail route.
    /// </summary>
    private static async Task<Results<Ok<SingleResponse<string>>, NotFound, ServiceUnavailableHttpResult>> HandleLookupByFingerprintId(
        string fingerprintId,
        [FromServices] IFingerprintReader? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        var signature = await store.LookupSignatureForFingerprintAsync(fingerprintId, ct);
        if (string.IsNullOrEmpty(signature)) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(signature);
    }

    private static async Task<Results<Ok<PaginatedResponse<NearestFingerprint>>, ServiceUnavailableHttpResult>> HandleNearest(
        string primarySignature,
        [FromServices] IFingerprintReader? store,
        [FromServices] IOptions<BotDetectionOptions> opts,
        [FromQuery] int? k = null,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Identity layer");
        // Operator-configured NeighbourCount is both the default (when caller omits
        // ?k) and the ceiling (when caller passes a larger k). No magic numbers --
        // the cap is whatever the operator set in IdentityOptions.LooksLike.
        var cfg = opts.Value.Identity?.LooksLike ?? new IdentityLooksLikeOptions();
        var effectiveK = Math.Clamp(k ?? cfg.NeighbourCount, 1, cfg.NeighbourCount);
        var hits = await store.GetNearestForSignatureAsync(primarySignature, effectiveK, ct);
        return ApiEndpointHelpers.Paginated(hits, hits.Count);
    }
}
