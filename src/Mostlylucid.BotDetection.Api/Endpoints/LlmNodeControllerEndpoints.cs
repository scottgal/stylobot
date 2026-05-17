using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Models;
using Mostlylucid.BotDetection.Llm.Tunnel;

namespace Mostlylucid.BotDetection.Api.Endpoints;

internal static class LlmNodeControllerEndpoints
{
    internal static IEndpointRouteBuilder MapLlmNodeControllerEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/llm-nodes")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName);

        group.MapPost("/import", HandleImport);
        group.MapGet("/", HandleList);
        group.MapPost("/{nodeId}/test", HandleTest);
        group.MapDelete("/{nodeId}", HandleDelete);

        return endpoints;
    }

    private static Results<Ok<LlmNodeImportResponse>, BadRequest<ApiError>> HandleImport(
        LlmNodeImportRequest request,
        ILlmNodeRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionKey))
            return TypedResults.BadRequest(new ApiError("ConnectionKey is required."));

        LlmNodeDescriptor descriptor;
        LlmNodeImportResponse importResponse;
        try
        {
            (descriptor, importResponse) = LlmNodeImporter.ImportKey(request.ConnectionKey);
        }
        catch (FormatException ex)
        {
            return TypedResults.BadRequest(new ApiError(ex.Message));
        }

        registry.Replace(descriptor);
        return TypedResults.Ok(importResponse);
    }

    private static Ok<IReadOnlyList<LlmNodeSummary>> HandleList(ILlmNodeRegistry registry)
    {
        var nodes = registry.GetAll().Select(LlmNodeSummary.From).ToArray();
        return TypedResults.Ok<IReadOnlyList<LlmNodeSummary>>(nodes);
    }

    private static async Task<Results<Ok<LlmNodeTestResult>, NotFound<ApiError>>> HandleTest(
        string nodeId,
        ILlmNodeRegistry registry,
        IHttpClientFactory httpClientFactory)
    {
        var node = registry.Get(nodeId);
        if (node is null)
            return TypedResults.NotFound(new ApiError($"Node '{nodeId}' not found."));

        using var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            var healthUrl = node.TunnelUrl.TrimEnd('/') + "/api/v1/llm-tunnel/health";
            var resp = await client.GetAsync(healthUrl);
            if (resp.IsSuccessStatusCode)
            {
                var updated = new LlmNodeDescriptor
                {
                    NodeId = node.NodeId,
                    Name = node.Name,
                    TunnelUrl = node.TunnelUrl,
                    TunnelKind = node.TunnelKind,
                    Provider = node.Provider,
                    Models = node.Models,
                    AdvertisedModels = node.AdvertisedModels,
                    KeyId = node.KeyId,
                    ControllerSharedSecret = node.ControllerSharedSecret,
                    MaxConcurrency = node.MaxConcurrency,
                    MaxContext = node.MaxContext,
                    LastSeenAt = DateTime.UtcNow,
                    Enabled = node.Enabled,
                    QueueDepth = node.QueueDepth,
                    FailureCount = 0
                };
                registry.Replace(updated);
                return TypedResults.Ok(new LlmNodeTestResult(nodeId, "reachable", null, null));
            }
            return TypedResults.Ok(new LlmNodeTestResult(nodeId, "unreachable", (int)resp.StatusCode, null));
        }
        catch (Exception ex)
        {
            return TypedResults.Ok(new LlmNodeTestResult(nodeId, "unreachable", null, ex.Message));
        }
    }

    private static Results<NoContent, NotFound<ApiError>> HandleDelete(string nodeId, ILlmNodeRegistry registry)
    {
        var removed = registry.Remove(nodeId);
        return removed
            ? TypedResults.NoContent()
            : TypedResults.NotFound(new ApiError($"Node '{nodeId}' not found."));
    }
}

public sealed record LlmNodeSummary(
    string NodeId,
    string Name,
    string TunnelUrl,
    string TunnelKind,
    string Provider,
    IReadOnlyList<string> Models,
    bool Enabled,
    DateTime LastSeenAt,
    int QueueDepth,
    int FailureCount,
    int MaxConcurrency,
    int MaxContext)
{
    public static LlmNodeSummary From(LlmNodeDescriptor n) => new(
        n.NodeId, n.Name, n.TunnelUrl, n.TunnelKind, n.Provider, n.Models,
        n.Enabled, n.LastSeenAt, n.QueueDepth, n.FailureCount, n.MaxConcurrency, n.MaxContext);
}

public sealed record LlmNodeTestResult(string NodeId, string Status, int? StatusCode, string? Error);
