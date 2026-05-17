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
///     Read-only session surface for remote dashboards. Wraps <see cref="ISessionStore"/>.
///     Sessions are the primary behavioural unit; the dashboard's Sessions tab + the
///     per-signature drill-in both go through these endpoints in remote mode.
/// </summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sessions")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Sessions");

        group.MapGet("/recent", HandleRecent).WithName("GetRecentSessions").WithOpenApi();
        group.MapGet("/{signature}", HandleBySignature).WithName("GetSessionsBySignature").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ProblemHttpResult>> HandleRecent(
        [FromServices] ISessionStore? store,
        int limit = 50,
        bool? isBot = null,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Session store not enabled.", statusCode: 503);

        var sessions = await store.GetRecentSessionsAsync(Math.Min(limit, 200), isBot, ct);
        return TypedResults.Ok(new PaginatedResponse<PersistedSession>
        {
            Data = sessions,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = sessions.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ProblemHttpResult>> HandleBySignature(
        string signature,
        [FromServices] ISessionStore? store,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Session store not enabled.", statusCode: 503);

        var sessions = await store.GetSessionsAsync(signature, Math.Min(limit, 100), ct);
        return TypedResults.Ok(new PaginatedResponse<PersistedSession>
        {
            Data = sessions,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = sessions.Count },
            Meta = new ResponseMeta()
        });
    }
}
