using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
/// Maps the operational persistence backpressure counters used by the soak harness.
/// </summary>
public static class PersistenceStatsEndpoints
{
    public static IEndpointRouteBuilder MapPersistenceStatsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/persistence-stats", ([FromServices] RequestPersistenceService svc) =>
        {
            var json = $"{{\"pendingWrites\":{svc.PendingWrites},\"writeStateCount\":{svc.WriteStateCount},\"writtenAlways\":{svc.WrittenAlwaysCount},\"writtenSampledIn\":{svc.WrittenSampledInCount},\"droppedSampledOut\":{svc.DroppedSampledOutCount}}}";
            return Results.Text(json, "application/json");
        }).RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
          .ExcludeFromDescription();

        return endpoints;
    }
}
