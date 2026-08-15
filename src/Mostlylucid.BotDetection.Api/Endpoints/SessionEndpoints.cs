using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

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

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ServiceUnavailableHttpResult>> HandleRecent(
        [FromServices] IDashboardEventStore? store,
        int limit = 50,
        bool? isBot = null,
        string? since = null,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        DateTime? sinceUtc = null;
        if (!string.IsNullOrEmpty(since) && DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            var candidate = parsed.ToUniversalTime();
            // Clamp to 30-day max history so callers cannot retrieve arbitrarily old sessions.
            var floor = DateTime.UtcNow.AddDays(-30);
            sinceUtc = candidate < floor ? floor : candidate;
        }
        var folds = await store.GetSessionFoldSummariesAsync(Math.Min(limit, 200), isBot, sinceUtc ?? DateTime.UtcNow.AddDays(-30), ct: ct);
        return ApiEndpointHelpers.Paginated(folds.Select(ToPersistedSession).ToList(), limit);
    }

    private static async Task<Results<Ok<PaginatedResponse<PersistedSession>>, ServiceUnavailableHttpResult>> HandleBySignature(
        string signature,
        [FromServices] IDashboardEventStore? store,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (store is null) return ApiEndpointHelpers.StoreUnavailable("Session store");
        var folds = await store.GetSessionFoldSummariesAsync(
            Math.Min(limit, 100), null, DateTime.UtcNow.AddDays(-30), signature: signature, ct: ct);
        return ApiEndpointHelpers.Paginated(folds.Select(ToPersistedSession).ToList(), limit);
    }

    /// <summary>
    ///     Map a window-fold session summary onto the <see cref="PersistedSession"/> DTO
    ///     (Phase B of the write-path grain redesign — the sessions read surface re-points
    ///     at the folds; the JSON contract is unchanged). The per-session analytic baggage
    ///     (vectors, transitions, timing entropy) retired with the sessions row and
    ///     degrades to defaults.
    /// </summary>
    private static PersistedSession ToPersistedSession(DashboardSessionFoldSummary s) => new()
    {
        Id = s.Id,
        Signature = s.Signature,
        StartedAt = s.StartedAt,
        EndedAt = s.EndedAt,
        RequestCount = s.RequestCount,
        Vector = Array.Empty<byte>(),
        Maturity = 0,
        DominantState = "unknown",
        IsBot = s.IsBot,
        AvgBotProbability = s.BotProbability,
        AvgConfidence = s.Confidence,
        RiskBand = s.RiskBand ?? "Unknown",
        Action = s.Action,
        BotName = s.BotName,
        BotType = s.BotType,
        CountryCode = s.CountryCode,
        AvgProcessingTimeMs = s.RequestCount > 0 ? s.MsSum / s.RequestCount : 0,
        ErrorCount = 0,
        TimingEntropy = 0,
    };
}
