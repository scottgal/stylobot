using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MetricsSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapMetricsSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_sb/metrics/snapshot", HandleSnapshot)
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .ExcludeFromDescription();

        return endpoints;
    }

    private static Results<Ok<MetricSnapshotDto[]>, StatusCodeHttpResult> HandleSnapshot(HttpContext ctx)
    {
        var accumulator = ctx.RequestServices.GetService<GatewayMeterAccumulator>();
        if (accumulator == null)
            return TypedResults.StatusCode(503);
        return TypedResults.Ok(accumulator.GetCurrentSnapshot());
    }
}
