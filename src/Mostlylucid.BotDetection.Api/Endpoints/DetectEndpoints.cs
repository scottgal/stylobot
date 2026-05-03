using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Bridge;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Endpoints;

public static class DetectEndpoints
{
    public static IEndpointRouteBuilder MapDetectEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName)
            .WithTags("Detection");

        group.MapPost("/detect", HandleDetect).WithName("Detect").WithOpenApi();
        group.MapPost("/detect/batch", HandleDetectBatch).WithName("DetectBatch").WithOpenApi();

        return endpoints;
    }

    private static async Task<IResult> HandleDetect(
        DetectRequest request,
        BlackboardOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var httpContext = SyntheticHttpContext.FromDetectRequest(request);
        var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
        return Results.Ok(DetectResponse.FromEvidence(evidence));
    }

    private static async Task<IResult> HandleDetectBatch(
        DetectRequest[] requests,
        BlackboardOrchestrator orchestrator,
        StyloBotApiOptions apiOptions,
        CancellationToken cancellationToken)
    {
        if (requests.Length > apiOptions.MaxBatchSize)
        {
            return Results.Problem(
                title: "Batch too large",
                detail: $"Maximum batch size is {apiOptions.MaxBatchSize}, got {requests.Length}",
                statusCode: 400,
                type: "https://stylobot.net/errors/batch-too-large");
        }

        var responses = new DetectResponse[requests.Length];
        for (var i = 0; i < requests.Length; i++)
        {
            var httpContext = SyntheticHttpContext.FromDetectRequest(requests[i]);
            var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
            responses[i] = DetectResponse.FromEvidence(evidence);
        }

        return Results.Ok(responses);
    }
}
