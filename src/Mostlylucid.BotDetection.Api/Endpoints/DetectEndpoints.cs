using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Bridge;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;

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
        IServiceScopeFactory scopes,
        IOptions<BotDetectionOptions> options,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<BotDetectionOrchestrator>();
        var httpContext = SyntheticHttpContext.FromDetectRequest(request);
        httpContext.RequestServices = scope.ServiceProvider;
        var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
        return TypedResults.Ok(DetectResponse.FromEvidence(evidence, options.Value.Classification.BotFloor));
    }

    private static async Task<Results<Ok<DetectResponse[]>, BadRequestProblemHttpResult>> HandleDetectBatch(
        DetectRequest[] requests,
        IServiceScopeFactory scopes,
        IOptions<BotDetectionOptions> options,
        StyloBotApiOptions apiOptions,
        CancellationToken cancellationToken)
    {
        if (requests.Length > apiOptions.MaxBatchSize)
        {
            return BadRequestProblemHttpResult.From(
                title: "Batch too large",
                detail: $"Maximum batch size is {apiOptions.MaxBatchSize}, got {requests.Length}",
                type: "https://stylo.bot/errors/batch-too-large");
        }

        var responses = new DetectResponse[requests.Length];
        for (var i = 0; i < requests.Length; i++)
        {
            // Each synthetic request owns a fresh signal sink; neither the
            // outer API caller nor a previous batch item may seed its verdict.
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<BotDetectionOrchestrator>();
            var httpContext = SyntheticHttpContext.FromDetectRequest(requests[i]);
            httpContext.RequestServices = scope.ServiceProvider;
            var evidence = await orchestrator.DetectAsync(httpContext, cancellationToken);
            responses[i] = DetectResponse.FromEvidence(evidence, options.Value.Classification.BotFloor);
        }

        return TypedResults.Ok(responses);
    }
}
