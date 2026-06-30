using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     <c>GET /api/v1/site-health/history?window={15m|1h|6h|12h|24h}</c>
///     -- returns persisted <see cref="DegradationSnapshot"/> rows from
///     <see cref="IDashboardEventStore.GetDegradationHistoryAsync"/> sliced
///     to the requested window, oldest-first. Backs the Traffic page's
///     site-health chartlet (rendered side-by-side with hits-per-period).
///     <para>
///         The store is always registered when the FOSS dashboard is in
///         the host (SQLite by default, Postgres on commercial). 503 is
///         no longer reachable for this surface because the store is the
///         dashboard's canonical truth source, not an opt-in atom.
///     </para>
///     <para>
///         Replaces the deleted in-memory <c>DegradationHistoryAtom</c>
///         ring per <c>feedback_no_inmemory_stores</c>: restart no longer
///         loses the history window.
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

    private static async Task<Results<Ok<PaginatedResponse<DegradationSnapshot>>, ProblemHttpResult>> HandleHistory(
        [FromServices] IDashboardEventStore store,
        string window = "1h",
        CancellationToken ct = default)
    {
        if (store is null)
            return TypedResults.Problem("Site-health history not enabled.", statusCode: 503);

        var span = ParseWindow(window);
        var now = DateTime.UtcNow;
        var data = await store.GetDegradationHistoryAsync(now - span, now, ct);
        return TypedResults.Ok(new PaginatedResponse<DegradationSnapshot>
        {
            Data = data,
            Pagination = new PaginationInfo { Offset = 0, Limit = data.Count, Total = data.Count },
            Meta = new ResponseMeta()
        });
    }

    /// <summary>
    ///     Map the dashboard's canonical window tokens onto a
    ///     <see cref="TimeSpan"/>. Unknown values fall back to 6 hours
    ///     because the Traffic page's default window is 6h (UX1).
    /// </summary>
    internal static TimeSpan ParseWindow(string window) => window switch
    {
        "15m"            => TimeSpan.FromMinutes(15),
        "1h" or "60m"    => TimeSpan.FromHours(1),
        "6h"             => TimeSpan.FromHours(6),
        "12h"            => TimeSpan.FromHours(12),
        "24h" or "1d"    => TimeSpan.FromHours(24),
        "7d"             => TimeSpan.FromDays(7),
        _                => TimeSpan.FromHours(6),
    };
}
