using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only cluster surface for remote dashboards. Wraps the in-process
///     <see cref="BotClusterService"/>; the service is a singleton BackgroundService that
///     holds the latest community-detection snapshot in memory.
/// </summary>
public static class ClusterEndpoints
{
    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/clusters")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Clusters");

        group.MapGet("", HandleList).WithName("GetClusters").WithOpenApi();
        group.MapGet("/diagnostics", HandleDiagnostics).WithName("GetClusterDiagnostics").WithOpenApi();

        return endpoints;
    }

    private static Results<Ok<PaginatedResponse<BotCluster>>, ProblemHttpResult> HandleList(
        [FromServices] IBotClusterReader? service)
    {
        if (service is null)
            return TypedResults.Problem("Cluster service not enabled.", statusCode: 503);

        var clusters = service.GetClusters();
        return TypedResults.Ok(new PaginatedResponse<BotCluster>
        {
            Data = clusters,
            Pagination = new PaginationInfo { Offset = 0, Limit = clusters.Count, Total = clusters.Count },
            Meta = new ResponseMeta()
        });
    }

    private static Results<Ok<SingleResponse<BotClusterService.ClusterDiagnosticsSnapshot>>, ProblemHttpResult> HandleDiagnostics(
        [FromServices] IBotClusterReader? service)
    {
        if (service is null)
            return TypedResults.Problem("Cluster service not enabled.", statusCode: 503);

        var diag = service.GetDiagnostics();
        return TypedResults.Ok(new SingleResponse<BotClusterService.ClusterDiagnosticsSnapshot>
        {
            Data = diag,
            Meta = new ResponseMeta()
        });
    }
}
