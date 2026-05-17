using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class ClusterEndpoints
{
    public static IEndpointRouteBuilder MapClusterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/clusters")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Clusters");

        group.MapGet("", HandleList).WithName("GetClusters");
        group.MapGet("/diagnostics", HandleDiagnostics).WithName("GetClusterDiagnostics");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<BotCluster>>, ProblemHttpResult>> HandleList(
        [FromServices] IBotClusterReader? service,
        CancellationToken ct = default)
    {
        if (service is null) return ApiEndpointHelpers.StoreUnavailable("Cluster service");
        var clusters = await service.GetClustersAsync(ct);
        return ApiEndpointHelpers.Paginated(clusters, clusters.Count);
    }

    private static async Task<Results<Ok<SingleResponse<BotClusterService.ClusterDiagnosticsSnapshot>>, ProblemHttpResult>> HandleDiagnostics(
        [FromServices] IBotClusterReader? service,
        CancellationToken ct = default)
    {
        if (service is null) return ApiEndpointHelpers.StoreUnavailable("Cluster service");
        return ApiEndpointHelpers.Single(await service.GetDiagnosticsAsync(ct));
    }
}
