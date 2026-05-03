using System.Threading;
using Mostlylucid.BotDetection.Llm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// Holds the runtime state of the local LLM agent, registered as a singleton in DI.
/// </summary>
public sealed class LocalLlmAgentContext
{
    public required string NodeId { get; init; }
    public required string KeyId { get; init; }
    public required byte[] SigningSecret { get; init; }
    public required string Provider { get; init; }
    public required LlmNodeModelInventory ModelInventory { get; init; }
    public required int MaxConcurrency { get; init; }
    public required int MaxContext { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    private int _queueDepth;
    public int QueueDepth => _queueDepth;
    public void IncrementQueue() => Interlocked.Increment(ref _queueDepth);
    public void DecrementQueue() => Interlocked.Decrement(ref _queueDepth);
}

/// <summary>
/// ASP.NET Core Minimal API endpoints for the local LLM tunnel agent.
/// Handles health, model inventory, and signed inference requests.
/// </summary>
public static class LocalLlmAgentEndpoints
{
    public static IEndpointRouteBuilder MapLocalLlmAgentEndpoints(
        this IEndpointRouteBuilder app, string basePath = "/api/v1/llm-tunnel")
    {
        var group = app.MapGroup(basePath);
        group.MapGet("/health", HandleHealth);
        group.MapGet("/models", HandleModels);
        group.MapPost("/complete", HandleComplete);
        return app;
    }

    private static IResult HandleHealth(LocalLlmAgentContext ctx)
    {
        var response = new LlmTunnelHealthResponse
        {
            Status = "ready",
            NodeId = ctx.NodeId,
            Provider = ctx.Provider,
            Version = "1",
            KeyId = ctx.KeyId,
            QueueDepth = ctx.QueueDepth,
            MaxConcurrency = ctx.MaxConcurrency,
            StartedAt = ctx.StartedAt
        };
        return Results.Ok(response);
    }

    private static IResult HandleModels(LocalLlmAgentContext ctx)
        => Results.Ok(ctx.ModelInventory);

    private static async Task<IResult> HandleComplete(
        LlmSignedInferenceRequest req,
        LocalLlmAgentContext ctx,
        LocalLlmTunnelCrypto crypto,
        ILlmProvider llmProvider,
        CancellationToken ct)
    {
        // 1. Validate binding (node id, key id)
        if (!LocalLlmTunnelCrypto.ValidateBinding(req, ctx.NodeId, ctx.KeyId))
            return Results.Problem("Invalid node or key binding.", statusCode: 401);

        // 2. Check expiry
        if (LocalLlmTunnelCrypto.IsRequestExpired(req))
            return Results.Problem("Request has expired.", statusCode: 401);

        // 3. Verify signature
        if (!crypto.VerifyRequest(req, ctx.SigningSecret))
            return Results.Problem("Invalid request signature.", statusCode: 401);

        // 4. Nonce replay protection
        if (!crypto.TryConsumeNonce(req.Nonce, req.ExpiresAt))
            return Results.Problem("Nonce already used (replay detected).", statusCode: 401);

        // 5. Validate model is advertised
        var modelAllowed = ctx.ModelInventory.Models
            .Any(m => string.Equals(m.Id, req.Payload.Model, StringComparison.OrdinalIgnoreCase));
        if (!modelAllowed)
            return Results.Problem($"Model '{req.Payload.Model}' is not advertised by this agent.", statusCode: 422);

        // 6. Validate context length
        if (req.Payload.MaxTokens > ctx.MaxContext)
            return Results.Problem($"MaxTokens {req.Payload.MaxTokens} exceeds agent limit {ctx.MaxContext}.", statusCode: 422);

        // 7. Concurrency cap
        if (ctx.QueueDepth >= ctx.MaxConcurrency)
            return Results.Problem("Agent at maximum concurrency.", statusCode: 503);

        // 8. Execute inference
        ctx.IncrementQueue();
        var started = DateTimeOffset.UtcNow;
        try
        {
            var prompt = string.Join("\n", req.Payload.Messages
                .Select(m => $"{m.Role}: {m.Content}"));

            var llmReq = new LlmRequest
            {
                Prompt = prompt,
                Temperature = req.Payload.Temperature,
                MaxTokens = req.Payload.MaxTokens,
                TimeoutMs = req.Payload.TimeoutMs > 0 ? req.Payload.TimeoutMs : 5000
            };

            var content = await llmProvider.CompleteAsync(llmReq, ct);
            var latencyMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

            var resp = new LlmSignedInferenceResponse
            {
                RequestId = req.RequestId,
                Model = req.Payload.Model,
                Content = content,
                LatencyMs = latencyMs,
                Signature = ""
            };
            resp.Signature = crypto.SignResponse(resp, ctx.SigningSecret);

            return Results.Ok(resp);
        }
        finally
        {
            ctx.DecrementQueue();
        }
    }
}
