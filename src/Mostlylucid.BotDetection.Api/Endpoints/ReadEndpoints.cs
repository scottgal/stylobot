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
        [FromServices] IDashboardEventStore store, int limit = 50, int offset = 0, bool? isBot = null, DateTime? since = null)
    {
        var filter = new DashboardFilter
        {
            Limit = Math.Min(limit, 200), Offset = offset, IsBot = isBot, StartTime = since
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
        [FromServices] IDashboardEventStore store, int limit = 100, int offset = 0, bool? isBot = null)
    {
        var signatures = await store.GetSignaturesAsync(limit, offset, isBot);
        return TypedResults.Ok(new PaginatedResponse<DashboardSignatureEvent>
        {
            Data = signatures,
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = signatures.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<SingleResponse<DashboardSummary>>> HandleSummary([FromServices] IDashboardEventStore store)
    {
        var summary = await store.GetSummaryAsync();
        return TypedResults.Ok(new SingleResponse<DashboardSummary> { Data = summary, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTimeSeriesPoint>>> HandleTimeseries(
        [FromServices] IDashboardEventStore store, string interval = "5m", DateTime? since = null, DateTime? until = null)
    {
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
        [FromServices] IDashboardEventStore store, int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        var countries = await store.GetCountryStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardCountryStats>
        {
            Data = countries,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = countries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardCountryDetail>>, NotFound>> HandleCountryDetail(
        string code, [FromServices] IDashboardEventStore store, DateTime? since = null, DateTime? until = null)
    {
        var detail = await store.GetCountryDetailAsync(code, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardCountryDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardEndpointStats>>> HandleEndpoints(
        [FromServices] IDashboardEventStore store, int limit = 50, DateTime? since = null, DateTime? until = null)
    {
        var eps = await store.GetEndpointStatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardEndpointStats>
        {
            Data = eps,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = eps.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<Ok<SingleResponse<DashboardEndpointDetail>>, NotFound>> HandleEndpointDetail(
        string method, string path, [FromServices] IDashboardEventStore store, DateTime? since = null, DateTime? until = null)
    {
        var detail = await store.GetEndpointDetailAsync(method, "/" + path, since, until);
        if (detail is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DashboardEndpointDetail> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<Ok<PaginatedResponse<DashboardTopBotEntry>>> HandleTopBots(
        [FromServices] IDashboardEventStore store, int limit = 10, DateTime? since = null, DateTime? until = null)
    {
        var bots = await store.GetTopBotsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<DashboardTopBotEntry>
        {
            Data = bots,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = bots.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Ok<PaginatedResponse<ThreatEntry>>> HandleThreats(
        [FromServices] IDashboardEventStore store, int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        var threats = await store.GetThreatsAsync(limit, since, until);
        return TypedResults.Ok(new PaginatedResponse<ThreatEntry>
        {
            Data = threats,
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = threats.Count },
            Meta = new ResponseMeta()
        });
    }
}
