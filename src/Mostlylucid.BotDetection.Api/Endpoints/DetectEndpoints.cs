using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
            .WithTags("Detection")
            .WithApiBotPolicy();

        group.MapPost("/detect", HandleDetect).WithName("Detect");
        group.MapPost("/detect/batch", HandleDetectBatch).WithName("DetectBatch");

        return endpoints;
    }

    private static async Task<Ok<DetectResponse>> HandleDetect(
        DetectRequest request,
        BlackboardOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var httpContext = SyntheticHttpContext.FromDetectRequest(request);
        var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
        return TypedResults.Ok(DetectResponse.FromEvidence(evidence));
    }

    private static async Task<Results<Ok<DetectResponse[]>, ProblemHttpResult>> HandleDetectBatch(
        DetectRequest[] requests,
        BlackboardOrchestrator orchestrator,
        StyloBotApiOptions apiOptions,
        CancellationToken cancellationToken)
    {
        if (requests.Length > apiOptions.MaxBatchSize)
        {
            return TypedResults.Problem(
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

        return TypedResults.Ok(responses);
    }
}
