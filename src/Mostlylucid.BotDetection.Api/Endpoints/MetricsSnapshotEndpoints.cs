using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MetricsSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapMetricsSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_sb/metrics/snapshot", (HttpContext ctx) =>
        {
            var accumulator = ctx.RequestServices.GetService<GatewayMeterAccumulator>();
            if (accumulator == null)
                return Results.StatusCode(503);
            return Results.Ok(accumulator.GetCurrentSnapshot());
        })
        .ExcludeFromDescription();

        return endpoints;
    }
}
