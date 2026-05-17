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
///     Read-only fingerprint-approval surface for remote dashboards. Wraps
///     <see cref="IFingerprintApprovalStore"/>. Write paths (Upsert / Revoke / token
///     generation) stay local to the gateway that owns the approvals database.
/// </summary>
public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/approvals")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Approvals");

        group.MapGet("", HandleList).WithName("GetApprovals").WithOpenApi();
        group.MapGet("/{signature}", HandleGet).WithName("GetApproval").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<ApprovalRecord>>, ProblemHttpResult>> HandleList(
        [FromServices] IFingerprintApprovalStore? store,
        int limit = 50,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Approval store not enabled.", statusCode: 503);

        var records = await store.ListRecentAsync(Math.Min(limit, 200), ct);
        return TypedResults.Ok(new PaginatedResponse<ApprovalRecord>
        {
            Data = records,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = records.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<ApprovalRecord>>, NotFound, ProblemHttpResult>> HandleGet(
        string signature,
        [FromServices] IFingerprintApprovalStore? store,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Approval store not enabled.", statusCode: 503);

        var record = await store.GetAsync(signature, ct);
        if (record is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<ApprovalRecord>
        {
            Data = record,
            Meta = new ResponseMeta()
        });
    }
}
