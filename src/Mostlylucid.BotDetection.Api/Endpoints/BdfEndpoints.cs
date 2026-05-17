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
        if (exporter is null) return ApiEndpointHelpers.StoreUnavailable("BDF export service");
        var doc = await exporter.ExportAsync(signature);
        if (doc is null) return TypedResults.NotFound();
        return ApiEndpointHelpers.Single(doc);
    }
}
