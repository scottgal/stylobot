using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/metrics")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Metrics");

        group.MapGet("/timeseries", HandleTimeseries)
            .WithName("GetMetricsTimeseries");

        group.MapGet("/latest", HandleLatest)
            .WithName("GetMetricsLatest");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<MetricSnapshot>>, ProblemHttpResult>> HandleTimeseries(
        [FromServices] IMetricSnapshotStore? store,
        string packId = "aspnet-monitoring",
        string instrument = "botdetection.requests.total",
        string range = "1h",
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Monitoring pack not enabled.", statusCode: 503);

        var end = DateTime.UtcNow;
        var start = range switch
        {
            "15m" => end.AddMinutes(-15),
            "1h"  => end.AddHours(-1),
            "6h"  => end.AddHours(-6),
            "24h" => end.AddHours(-24),
            _     => end.AddHours(-1)
        };

        var data = await store.GetTimeSeriesAsync(packId, instrument, start, end, ct);
        return TypedResults.Ok(new PaginatedResponse<MetricSnapshot>
        {
            Data = data,
            Pagination = new PaginationInfo { Offset = 0, Limit = data.Count, Total = data.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<IReadOnlyList<MetricSnapshot>>>, ProblemHttpResult>> HandleLatest(
        [FromServices] IMetricSnapshotStore? store,
        string packId = "aspnet-monitoring",
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Monitoring pack not enabled.", statusCode: 503);

        var data = await store.GetLatestSnapshotsAsync(packId, ct);
        return TypedResults.Ok(new SingleResponse<IReadOnlyList<MetricSnapshot>>
        {
            Data = data,
            Meta = new ResponseMeta()
        });
    }
}
