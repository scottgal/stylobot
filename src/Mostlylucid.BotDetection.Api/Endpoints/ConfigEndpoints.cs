using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/config")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Configuration")
            .WithApiBotPolicy();

        group.MapGet("/manifests", HandleList).WithName("GetConfigManifests");
        group.MapGet("/manifests/{slug}", HandleGet).WithName("GetConfigManifest");

        return endpoints;
    }

    private static async Task<Results<Ok<PaginatedResponse<DetectorManifestSummary>>, ServiceUnavailableHttpResult>> HandleList(
        [FromServices] IConfigEditorService? editor,
        CancellationToken ct = default)
    {
        if (editor is null) return ApiEndpointHelpers.StoreUnavailable("Config editor service");
        var manifests = await editor.ListManifestsAsync(ct);
        return ApiEndpointHelpers.Paginated(manifests, manifests.Count);
    }

    private static async Task<Results<Ok<SingleResponse<DetectorManifestDocument>>, NotFound, ServiceUnavailableHttpResult>> HandleGet(
        string slug,
        [FromServices] IConfigEditorService? editor,
        CancellationToken ct = default)
    {
        if (editor is null) return ApiEndpointHelpers.StoreUnavailable("Config editor service");
        var doc = await editor.GetManifestAsync(slug, ct);
        if (doc is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(doc);
    }
}
