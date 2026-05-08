using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class MetricsSnapshotEndpoints
{
    public static IEndpointRouteBuilder MapMetricsSnapshotEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_sb/metrics/snapshot", (GatewayMeterAccumulator accumulator) =>
            Results.Ok(accumulator.GetCurrentSnapshot()))
        .ExcludeFromDescription();

        return endpoints;
    }
}
