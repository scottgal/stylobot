using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sessions")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Sessions")
            .WithApiBotPolicy();

        group.MapGet("/recent", HandleRecent).WithName("GetRecentSessions");
        group.MapGet("/{signature}", HandleBySignature).WithName("GetSessionsBySignature");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ProblemHttpResult>> HandleRecent(
        [FromServices] ISessionStore? store,
        int limit = 50,
        bool? isBot = null,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        var sessions = await store.GetRecentSessionsAsync(Math.Min(limit, 200), isBot, ct);
        return ApiEndpointHelpers.Paginated(sessions, limit);
    }

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ProblemHttpResult>> HandleBySignature(
        string signature,
        [FromServices] ISessionStore? store,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        var sessions = await store.GetSessionsAsync(signature, Math.Min(limit, 100), ct);
        return ApiEndpointHelpers.Paginated(sessions, limit);
    }
}
