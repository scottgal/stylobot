using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/approvals")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Approvals")
            .WithApiBotPolicy();

        group.MapGet("", HandleList).WithName("GetApprovals");
        group.MapGet("/{signature}", HandleGet).WithName("GetApproval");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<ApprovalRecord>>, ProblemHttpResult>> HandleList(
        [FromServices] IFingerprintApprovalStore? store,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Approval store");
        var records = await store.ListRecentAsync(Math.Min(limit, 200), ct);
        return ApiEndpointHelpers.Paginated(records, limit);
    }

    private static async Task<Results<Ok<SingleResponse<ApprovalRecord>>, NotFound, ProblemHttpResult>> HandleGet(
        string signature,
        [FromServices] IFingerprintApprovalStore? store,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Approval store");
        var record = await store.GetAsync(signature, ct);
        if (record is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(record);
    }
}
