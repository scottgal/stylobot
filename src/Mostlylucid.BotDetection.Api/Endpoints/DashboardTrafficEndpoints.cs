using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class DashboardTrafficEndpoints
{
    public static IEndpointRouteBuilder MapDashboardTrafficEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/v1/dashboard")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithApiBotPolicy()
            .MapGet("/traffic", ReadTraffic).WithName("GetDashboardTraffic").WithTags("Dashboard Data");
        return endpoints;
    }

    private static async Task<Results<Ok<TrafficSnapshot>, ValidationProblem, ProblemHttpResult>> ReadTraffic(
        HttpContext context, [FromServices] TrafficQueryService service,
        [FromServices] ILoggerFactory loggers, CancellationToken ct,
        string window = "24h", DateTimeOffset? from = null, DateTimeOffset? to = null,
        [FromQuery(Name = "domain")] string[]? domains = null, bool includeInternal = false, int limit = 50)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-StyloBot-TraceId"] = context.TraceIdentifier;
        try
        {
            return TypedResults.Ok(await service.ReadAsync(new(window, from, to, domains, includeInternal, limit), ct));
        }
        catch (TrafficQueryValidationException ex)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "query"] = [ex.Message] });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            loggers.CreateLogger("StyloBot.Dashboard.Traffic").LogError(ex,
                "Traffic snapshot read failed. TraceId: {TraceId}", context.TraceIdentifier);
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Traffic data is unavailable.", extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier });
        }
    }
}
