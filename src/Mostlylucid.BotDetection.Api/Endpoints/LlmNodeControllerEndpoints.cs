using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Llm.Tunnel;

namespace Mostlylucid.BotDetection.Api.Endpoints;

internal static class LlmNodeControllerEndpoints
{
    internal static IEndpointRouteBuilder MapLlmNodeControllerEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/llm-nodes")
            .RequireAuthorization(ApiKeyAuthenticationHandler.SchemeName);

        // Import a connection key
        group.MapPost("/import", async (
            LlmNodeImportRequest request,
            ILlmNodeRegistry registry) =>
        {
            if (string.IsNullOrWhiteSpace(request.ConnectionKey))
                return Results.BadRequest("ConnectionKey is required.");

            LlmNodeDescriptor descriptor;
            LlmNodeImportResponse importResponse;
            try
            {
                (descriptor, importResponse) = LlmNodeImporter.ImportKey(request.ConnectionKey);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            // Replace if already registered (re-import / rotation)
            registry.Replace(descriptor);
            return Results.Ok(importResponse);
        });

        // List all registered nodes (sensitive fields omitted)
        group.MapGet("/", (ILlmNodeRegistry registry) =>
        {
            var nodes = registry.GetAll().Select(n => new
            {
                n.NodeId,
                n.Name,
                n.TunnelUrl,
                n.TunnelKind,
                n.Provider,
                n.Models,
                n.Enabled,
                n.LastSeenAt,
                n.QueueDepth,
                n.FailureCount,
                n.MaxConcurrency,
                n.MaxContext
            });
            return Results.Ok(nodes);
        });

        // Test connectivity to a node
        group.MapPost("/{nodeId}/test", async (
            string nodeId,
            ILlmNodeRegistry registry,
            IHttpClientFactory httpClientFactory) =>
        {
            var node = registry.Get(nodeId);
            if (node is null)
                return Results.NotFound($"Node '{nodeId}' not found.");

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                var healthUrl = node.TunnelUrl.TrimEnd('/') + "/api/v1/llm-tunnel/health";
                var resp = await client.GetAsync(healthUrl);
                if (resp.IsSuccessStatusCode)
                {
                    // Update last seen
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
                    return Results.Ok(new { nodeId, status = "reachable" });
                }
                return Results.Ok(new { nodeId, status = "unreachable", statusCode = (int)resp.StatusCode });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { nodeId, status = "unreachable", error = ex.Message });
            }
        });

        // Revoke / delete a node
        group.MapDelete("/{nodeId}", (string nodeId, ILlmNodeRegistry registry) =>
        {
            var removed = registry.Remove(nodeId);
            return removed ? Results.NoContent() : Results.NotFound($"Node '{nodeId}' not found.");
        });

        return endpoints;
    }
}
