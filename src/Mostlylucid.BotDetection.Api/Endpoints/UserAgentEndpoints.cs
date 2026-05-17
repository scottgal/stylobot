using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     UA-family search for the dashboard's User Agents tab. Wraps
///     <see cref="IDashboardEventStore.SearchUserAgentsAsync"/>.
/// </summary>
public static class UserAgentEndpoints
{
    public static IEndpointRouteBuilder MapUserAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/useragents")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("User Agents");

        group.MapGet("/search", HandleSearch).WithName("SearchUserAgents").WithOpenApi();

        return endpoints;
    }

    private static async Task<Ok<PaginatedResponse<UserAgentSearchResult>>> HandleSearch(
        [FromServices] IDashboardEventStore store,
        string query = "",
        int limit = 20)
    {
        var results = await store.SearchUserAgentsAsync(query, Math.Min(limit, 100));
        return TypedResults.Ok(new PaginatedResponse<UserAgentSearchResult>
        {
            Data = results,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = results.Count },
            Meta = new ResponseMeta()
        });
    }
}
