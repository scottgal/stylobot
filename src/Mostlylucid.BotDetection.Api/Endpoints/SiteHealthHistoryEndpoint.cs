using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     <c>GET /api/v1/site-health/history?window={15m|1h|24h|7d}</c>
///     -- returns the bounded <see cref="DegradationHistoryAtom"/> ring sliced
///     to the requested window, oldest-first. Backs the Traffic page's
///     site-health chartlet (rendered side-by-side with hits-per-period).
///     <para>
///         503 when the atom is not registered (host has not opted into
///         rate-limit / degradation tracking). The dashboard's
///         <c>SbSiteHealthViewComponent</c> early-returns on null per
///         <c>feedback_remote_mode_optional_di</c>.
///     </para>
/// </summary>
public static class SiteHealthHistoryEndpoint
{
    public static IEndpointRouteBuilder MapSiteHealthHistoryEndpoint(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/site-health")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Site Health")
            .WithApiBotPolicy();

        group.MapGet("/history", HandleHistory)
            .WithName("GetSiteHealthHistory");

        return endpoints;
    }

    private static Results<Ok<PaginatedResponse<DegradationSnapshot>>, ProblemHttpResult> HandleHistory(
        [FromServices] DegradationHistoryAtom? history,
        string window = "1h")
    {
        if (history is null)
            return TypedResults.Problem("Site-health history not enabled.", statusCode: 503);

        var span = ParseWindow(window);
        var data = history.GetWindow(DateTime.UtcNow, span);
        return TypedResults.Ok(new PaginatedResponse<DegradationSnapshot>
        {
            Data = data,
            Pagination = new PaginationInfo { Offset = 0, Limit = data.Count, Total = data.Count },
            Meta = new ResponseMeta()
        });
    }

    /// <summary>
    ///     Map the dashboard's canonical window tokens onto a
    ///     <see cref="TimeSpan"/>. Unknown values fall back to 1 hour to
    ///     match the Traffic page's default selector.
    /// </summary>
    internal static TimeSpan ParseWindow(string window) => window switch
    {
        "15m"        => TimeSpan.FromMinutes(15),
        "1h" or "60m" => TimeSpan.FromHours(1),
        "24h" or "1d" => TimeSpan.FromHours(24),
        "7d"         => TimeSpan.FromDays(7),
        _            => TimeSpan.FromHours(1),
    };
}
