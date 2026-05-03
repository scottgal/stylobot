using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            .WithTags("Dashboard Data");

        group.MapGet("/detections", HandleDetections).WithName("GetDetections").WithOpenApi();
        group.MapGet("/signatures", HandleSignatures).WithName("GetSignatures").WithOpenApi();
        group.MapGet("/summary", HandleSummary).WithName("GetSummary").WithOpenApi();
        group.MapGet("/timeseries", HandleTimeseries).WithName("GetTimeseries").WithOpenApi();
        group.MapGet("/countries", HandleCountries).WithName("GetCountries").WithOpenApi();
        group.MapGet("/countries/{code}", HandleCountryDetail).WithName("GetCountryDetail").WithOpenApi();
        group.MapGet("/endpoints", HandleEndpoints).WithName("GetEndpoints").WithOpenApi();
        group.MapGet("/endpoints/{method}/{**path}", HandleEndpointDetail).WithName("GetEndpointDetail").WithOpenApi();
        group.MapGet("/topbots", HandleTopBots).WithName("GetTopBots").WithOpenApi();
        group.MapGet("/threats", HandleThreats).WithName("GetThreats").WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleDetections(
        IDashboardEventStore store, int limit = 50, int offset = 0, bool? isBot = null, DateTime? since = null)
    {
        var filter = new DashboardFilter
        {
            Limit = Math.Min(limit, 200), Offset = offset, IsBot = isBot, StartTime = since
        };
        var detections = await store.GetDetectionsAsync(filter);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = detections.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = detections.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleSignatures(
        IDashboardEventStore store, int limit = 100, int offset = 0, bool? isBot = null)
    {
        var signatures = await store.GetSignaturesAsync(limit, offset, isBot);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = signatures.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = offset, Limit = limit, Total = signatures.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleSummary(IDashboardEventStore store)
    {
        var summary = await store.GetSummaryAsync();
        return Results.Ok(new SingleResponse<object> { Data = summary, Meta = new ResponseMeta() });
    }

    private static async Task<IResult> HandleTimeseries(
        IDashboardEventStore store, string interval = "5m", DateTime? since = null, DateTime? until = null)
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
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = timeseries.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = timeseries.Count, Total = timeseries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleCountries(
        IDashboardEventStore store, int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        var countries = await store.GetCountryStatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = countries.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = countries.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleCountryDetail(
        string code, IDashboardEventStore store, DateTime? since = null, DateTime? until = null)
    {
        var detail = await store.GetCountryDetailAsync(code, since, until);
        if (detail is null) return Results.NotFound();
        return Results.Ok(new SingleResponse<object> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<IResult> HandleEndpoints(
        IDashboardEventStore store, int limit = 50, DateTime? since = null, DateTime? until = null)
    {
        var eps = await store.GetEndpointStatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = eps.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = eps.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleEndpointDetail(
        string method, string path, IDashboardEventStore store, DateTime? since = null, DateTime? until = null)
    {
        var detail = await store.GetEndpointDetailAsync(method, "/" + path, since, until);
        if (detail is null) return Results.NotFound();
        return Results.Ok(new SingleResponse<object> { Data = detail, Meta = new ResponseMeta() });
    }

    private static async Task<IResult> HandleTopBots(
        IDashboardEventStore store, int limit = 10, DateTime? since = null, DateTime? until = null)
    {
        var bots = await store.GetTopBotsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = bots.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = bots.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<IResult> HandleThreats(
        IDashboardEventStore store, int limit = 20, DateTime? since = null, DateTime? until = null)
    {
        var threats = await store.GetThreatsAsync(limit, since, until);
        return Results.Ok(new PaginatedResponse<object>
        {
            Data = threats.Cast<object>().ToList(),
            Pagination = new PaginationInfo { Offset = 0, Limit = limit, Total = threats.Count },
            Meta = new ResponseMeta()
        });
    }
}
