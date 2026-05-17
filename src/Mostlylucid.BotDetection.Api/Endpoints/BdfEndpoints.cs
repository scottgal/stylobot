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
///     Zero-PII Behavioural Definition Format export for a signature. Wraps
///     <see cref="BdfExportService"/>. Used by the dashboard's "export BDF" button
///     and by replay-rig tooling.
/// </summary>
public static class BdfEndpoints
{
    public static IEndpointRouteBuilder MapBdfEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/bdf")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("BDF Export");

        group.MapGet("/{signature}", HandleExport).WithName("ExportBdf").WithOpenApi();

        return endpoints;
    }

    private static async Task<Results<Ok<SingleResponse<BdfExportDocument>>, NotFound, ProblemHttpResult>> HandleExport(
        string signature,
        [FromServices] BdfExportService? exporter)
    {
        if (exporter is null)
            return TypedResults.Problem("BDF export service not enabled.", statusCode: 503);

        var doc = await exporter.ExportAsync(signature);
        if (doc is null) return TypedResults.NotFound();
        return TypedResults.Ok(new SingleResponse<BdfExportDocument>
        {
            Data = doc,
            Meta = new ResponseMeta()
        });
    }
}
