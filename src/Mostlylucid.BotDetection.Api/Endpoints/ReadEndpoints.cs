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

// Common-case (default-args) responses are served from
// DashboardAggregateCache, which DashboardSummaryBroadcaster refreshes every
// SummaryBroadcastIntervalSeconds. Filtered / windowed / paginated queries
// (custom since/until/offset/etc.) fall through to the store unchanged --
// the cache is a fast-path, not a gate.

public static class ReadEndpoints
{
    public static IEndpointRouteBuilder MapReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Dashboard Data")
            .WithApiBotPolicy();

        group.MapGet("/detections", HandleDetections).WithName("GetDetections");
        group.MapGet("/signatures", HandleSignatures).WithName("GetSignatures");
        group.MapGet("/summary", HandleSummary).WithName("GetSummary");
        group.MapGet("/timeseries", HandleTimeseries).WithName("GetTimeseries");
        group.MapGet("/countries", HandleCountries).WithName("GetCountries");
        group.MapGet("/countries/{code}", HandleCountryDetail).WithName("GetCountryDetail");
        group.MapGet("/endpoints", HandleEndpoints).WithName("GetEndpoints");
        group.MapGet("/endpoints/{method}/{**path}", HandleEndpointDetail).WithName("GetEndpointDetail");
        group.MapGet("/topbots", HandleTopBots).WithName("GetTopBots");
        group.MapGet("/threats", HandleThreats).WithName("GetThreats");

        return endpoints;
    }

    private static async Task<Ok<PaginatedResponse<DashboardDetectionEvent>>> HandleDetections(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 50, int offset = 0, bool? isBot = null, DateTime? since = null, string? signature = null)
    {
        aggregateCache.MarkHit();
        var cappedLimit = Math.Min(limit, 200);
        var snapshot = aggregateCache.Current;
        // The precomputed snapshot holds the most recent detections across ALL
        // signatures, so it can only short-circuit the unfiltered default view.
        // A signature-scoped query (the signature-detail page) must hit the store.
        if (offset == 0 && isBot is null && since is null && string.IsNullOrEmpty(signature)
            && snapshot.Detections.Count >= cappedLimit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.Detections.Take(cappedLimit).ToList();
            return TypedResults.Ok(new PaginatedResponse<DashboardDetectionEvent>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        var filter = new DashboardFilter
        {
            Limit = cappedLimit, Offset = offset, IsBot = isBot, StartTime = since,
            SignatureId = string.IsNullOrEmpty(signature) ? null : signature
        };
        var detections = await store.GetDetectionsAsync(filter);
        return TypedResults.Ok(new PaginatedResponse<DashboardDetectionEvent>
        {
            Data = detections,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = detections.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<PaginatedResponse<DashboardSignatureEvent>>> HandleSignatures(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 100, int offset = 0, bool? isBot = null)
    {
        aggregateCache.MarkHit();
        var signatures = await store.GetSignaturesAsync(limit, offset, isBot);
        return TypedResults.Ok(new PaginatedResponse<DashboardSignatureEvent>
        {
            Data = signatures,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = signatures.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<SingleResponse<DashboardSummary>>> HandleSummary(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        if (snapshot.Summary is not null && snapshot.ComputedAt != DateTime.MinValue)
            return TypedResults.Ok(new SingleResponse<DashboardSummary>
            {
                Data = snapshot.Summary, Meta = new ResponseMeta()
            });

        var summary = await store.GetSummaryAsync();
        return TypedResults.Ok(new SingleResponse<DashboardSummary> { Data = summary, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTimeSeriesPoint>>> HandleTimeseries(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        string interval = "5m", DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        // Default-view window only: trailing 24 h at 5-minute buckets.
        // Anything else gets the store path.
        if (since is null && until is null && interval == "5m"
            && snapshot.TimeSeries.Count > 0
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            return TypedResults.Ok(new PaginatedResponse<DashboardTimeSeriesPoint>
            {
                Data = snapshot.TimeSeries,
                Pagination = new PaginationInfo
                {
                    Offset = 0, Limit = snapshot.TimeSeries.Count, Total = snapshot.TimeSeries.Count
                },
                Meta = new ResponseMeta()
            });
        }

        var bucketSize = interval switch
        {
            "1m" => TimeSpan.FromMinutes(1), "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15), "1h" => TimeSpan.FromHours(1),
            _ => TimeSpan.FromMinutes(5)
        };
        var start = since ?? DateTime.UtcNow.AddHours(-24);
        var end = until ?? DateTime.UtcNow;
        var timeseries = await store.GetTimeSeriesAsync(start, end, bucketSize);
        return TypedResults.Ok(new PaginatedResponse<DashboardTimeSeriesPoint>
        {
            Data = timeseries,
            Pagination = new PaginationInfo { Offset = 0, Limit = timeseries.Count, Total = timeseries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<PaginatedResponse<DashboardCountryStats>>> HandleCountries(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var countries = await store.GetCountryStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardCountryStats>
        {
            Data = countries,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = countries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardCountryDetail>>, NotFound>> HandleCountryDetail(
        string code,
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var detail = await store.GetCountryDetailAsync(code, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardCountryDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardEndpointStats>>> HandleEndpoints(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 50, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var eps = await store.GetEndpointStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardEndpointStats>
        {
            Data = eps,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = eps.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardEndpointDetail>>, NotFound>> HandleEndpointDetail(
        string method, string path,
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var detail = await store.GetEndpointDetailAsync(method, "/" + path, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardEndpointDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTopBotEntry>>> HandleTopBots(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 10, DateTime? since = null, DateTime? until = null,
        string? audience = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        // Aggregate-cache fast path only applies to the legacy bots-only call (no window,
        // no audience). When the caller asks for "all" or "humans" we go direct to the
        // event store -- the cache only holds the precomputed top bots.
        if (since is null && until is null && string.IsNullOrEmpty(audience)
            && snapshot.TopBots.Count >= limit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.TopBots.Take(limit).ToList();
            return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        var bots = await store.GetTopBotsAsync(limit, since, until, audience);
        return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
        {
            Data = bots,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = bots.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<PaginatedResponse<ThreatEntry>>> HandleThreats(
        [FromServices] IDashboardEventStore store,
        [FromServices] DashboardAggregateCache aggregateCache,
        int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        aggregateCache.MarkHit();
        var snapshot = aggregateCache.Current;
        if (since is null && until is null
            && snapshot.Threats.Count >= limit
            && snapshot.ComputedAt != DateTime.MinValue)
        {
            var slice = snapshot.Threats.Take(limit).ToList();
            return TypedResults.Ok(new PaginatedResponse<ThreatEntry>
            {
                Data = slice,
                Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = slice.Count },
                Meta = new ResponseMeta()
            });
        }

        var threats = await store.GetThreatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<ThreatEntry>
        {
            Data = threats,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = threats.Count },
            Meta = new ResponseMeta()
        });
    }
}
