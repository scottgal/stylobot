using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Services.Routes;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class RoutesEndpoints
{
    public static IEndpointRouteBuilder MapRoutesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/routes")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Routes")
            .WithApiBotPolicy();

        group.MapGet("", HandleList)
            .WithName("ListRoutes");

        group.MapPut("/name", HandleSetName)
            .WithName("SetRouteName");

        group.MapDelete("/name", HandleRemoveName)
            .WithName("RemoveRouteName");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<RouteEntryDto>>, ServiceUnavailableHttpResult>> HandleList(
        [FromServices] IRouteCatalogService? catalog,
        CancellationToken ct = default)
    {
        if (catalog is null)
            return ServiceUnavailableHttpResult.FromTitle("Route catalog not enabled.");

        var entries = await catalog.GetCatalogAsync(ct);
        var data = entries.Select(RouteEntryDto.FromEntry).ToList();
        return TypedResults.Ok(new PaginatedResponse<RouteEntryDto>
        {
            Data = data,
            Pagination = new PaginationInfo { Offset = 0, Limit = data.Count, Total = data.Count },
            Meta = new ResponseMeta()
        });
    }

    private static async Task<Results<NoContent, BadRequest<ApiError>, ServiceUnavailableHttpResult>> HandleSetName(
        [FromServices] IRouteNameStore? store,
        [FromBody] SetRouteNameRequest body,
        HttpContext http,
        CancellationToken ct = default)
    {
        if (store is null)
            return ServiceUnavailableHttpResult.FromTitle("Route catalog not enabled.");
        if (string.IsNullOrWhiteSpace(body?.RouteKey))
            return TypedResults.BadRequest(new ApiError("routeKey is required"));
        if (string.IsNullOrWhiteSpace(body.FriendlyName))
            return TypedResults.BadRequest(new ApiError("friendlyName is required"));

        var updatedBy = http.User.Identity?.Name ?? "api-key";
        try
        {
            await store.SetAsync(body.RouteKey, body.FriendlyName, body.Notes, updatedBy, ct);
            return TypedResults.NoContent();
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(new ApiError(ex.Message));
        }
    }

    private static async Task<Results<NoContent, BadRequest<ApiError>, ServiceUnavailableHttpResult>> HandleRemoveName(
        [FromServices] IRouteNameStore? store,
        string routeKey,
        CancellationToken ct = default)
    {
        if (store is null)
            return ServiceUnavailableHttpResult.FromTitle("Route catalog not enabled.");
        if (string.IsNullOrWhiteSpace(routeKey))
            return TypedResults.BadRequest(new ApiError("routeKey is required"));

        await store.RemoveAsync(routeKey, ct);
        return TypedResults.NoContent();
    }
}

public sealed record SetRouteNameRequest(string RouteKey, string FriendlyName, string? Notes);

public sealed record RouteEntryDto(
    string RouteKey,
    string Pattern,
    IReadOnlyList<string> HttpMethods,
    string? DisplayName,
    string? FriendlyName,
    string? Notes,
    bool IsDiscovered,
    bool IsDocumented,
    bool RequiresAuthorization,
    bool AllowsAnonymous,
    string? Summary,
    string? Description,
    IReadOnlyList<string> Tags,
    string Category,
    string? OperationId,
    bool? OpenApiDeprecated,
    IReadOnlyList<int> OpenApiResponseStatusCodes,
    IReadOnlyList<string> OpenApiSecuritySchemes)
{
    public static RouteEntryDto FromEntry(RouteCatalogEntry e) => new(
        RouteKey: e.RouteKey,
        Pattern: e.Pattern,
        HttpMethods: e.HttpMethods,
        DisplayName: e.DisplayName,
        FriendlyName: e.FriendlyName,
        Notes: e.Notes,
        IsDiscovered: e.IsDiscovered,
        IsDocumented: e.IsDocumented,
        RequiresAuthorization: e.RequiresAuthorization,
        AllowsAnonymous: e.AllowsAnonymous,
        Summary: e.Summary,
        Description: e.Description,
        Tags: e.Tags,
        Category: e.Category.ToString(),
        OperationId: e.OpenApiOperation?.OperationId,
        OpenApiDeprecated: e.OpenApiOperation?.Deprecated,
        OpenApiResponseStatusCodes: e.OpenApiOperation?.ResponseStatusCodes ?? Array.Empty<int>(),
        OpenApiSecuritySchemes: e.OpenApiOperation?.SecuritySchemes ?? Array.Empty<string>());
}
