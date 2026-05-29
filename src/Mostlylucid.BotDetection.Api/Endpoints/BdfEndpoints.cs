using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class BdfEndpoints
{
    public static IEndpointRouteBuilder MapBdfEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/bdf")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("BDF Export")
            .WithApiBotPolicy();

        group.MapGet("/{signature}", HandleExport).WithName("ExportBdf");

        group.MapGet("/harvest", HandleHarvest)
            .WithName("HarvestBdfs")
            .Produces(200)
            .Produces(404);

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

    /// <summary>
    ///     Streams persisted BDFs as NDJSON for offline archetype-YAML authoring.
    ///     Gated by <see cref="BdfHarvestOptions.Enabled"/> -- the dev/debug switch --
    ///     and capped by <see cref="BdfHarvestOptions.MaxBatchSize"/> so a misconfigured
    ///     client can't drain the entire signature store in one call.
    /// </summary>
    private static async Task HandleHarvest(
        HttpContext context,
        [FromServices] IOptions<BdfHarvestOptions> options,
        [FromServices] BdfHarvestService harvest,
        CancellationToken ct,
        [FromQuery] DateTime? since = null,
        [FromQuery] int? limit = null,
        [FromQuery] string? labelBy = null)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"bdf harvest disabled\"}", ct);
            return;
        }

        var effectiveLimit = Math.Min(limit ?? 100, opts.MaxBatchSize);
        if (effectiveLimit < 1) effectiveLimit = 1;

        context.Response.ContentType = "application/x-ndjson";

        var newline = Encoding.UTF8.GetBytes("\n");
        await foreach (var entry in harvest.HarvestAsync(since, effectiveLimit, labelBy, ct))
        {
            await JsonSerializer.SerializeAsync(context.Response.Body, entry, cancellationToken: ct);
            await context.Response.Body.WriteAsync(newline, ct);
        }
    }
}