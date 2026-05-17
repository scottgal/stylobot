using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;

namespace Mostlylucid.BotDetection.Api.Endpoints;

/// <summary>
///     Read-only manifest surface for remote dashboards. Wraps
///     <see cref="IConfigEditorService"/>. The write surface (PUT override / DELETE revert)
///     stays on the dashboard middleware that owns the local filesystem.
/// </summary>
public static class ConfigEndpoints
{
    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/config")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Configuration");

        group.MapGet("/manifests", HandleList).WithName("GetConfigManifests").WithOpenApi();
        group.MapGet("/manifests/{slug}", HandleGet).WithName("GetConfigManifest").WithOpenApi();

        return endpoints;
    }

    private static Results<Ok<PaginatedResponse<DetectorManifestSummary>>, ProblemHttpResult> HandleList(
        [FromServices] IConfigEditorService? editor)
    {
        if (editor is null)
            return TypedResults.Problem("Config editor service not enabled.", statusCode: 503);

        var manifests = editor.ListManifests();
        return TypedResults.Ok(new PaginatedResponse<DetectorManifestSummary>
        {
            Data = manifests,
            Pagination = new PaginationInfo { Offset = 0, Limit = manifests.Count, Total = manifests.Count },
            Meta = new ResponseMeta()
        });
    }

    private static Results<Ok<SingleResponse<DetectorManifestDocument>>, NotFound, ProblemHttpResult> HandleGet(
        string slug,
        [FromServices] IConfigEditorService? editor)
    {
        if (editor is null)
            return TypedResults.Problem("Config editor service not enabled.", statusCode: 503);

        var doc = editor.GetManifest(slug);
        if (doc is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<DetectorManifestDocument>
        {
            Data = doc,
            Meta = new ResponseMeta()
        });
    }
}
