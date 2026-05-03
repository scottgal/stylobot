using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Llm;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// ILlmProvider that routes inference requests to a local GPU agent
/// through a Cloudflare tunnel, using signed request/response protocol.
/// </summary>
public sealed class LocalLlmTunnelClientProvider : ILlmProvider
{
    private const string HttpClientName = "stylobot-llm-tunnel-client";

    private readonly ILlmNodeRegistry _registry;
    private readonly LocalLlmTunnelOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LocalLlmTunnelCrypto _crypto;
    private readonly ILogger<LocalLlmTunnelClientProvider> _logger;

    public LocalLlmTunnelClientProvider(
        ILlmNodeRegistry registry,
        IOptions<LocalLlmTunnelOptions> options,
        IHttpClientFactory httpClientFactory,
        LocalLlmTunnelCrypto crypto,
        ILogger<LocalLlmTunnelClientProvider> logger)
    {
        _registry = registry;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _crypto = crypto;
        _logger = logger;
    }

    public bool IsReady
    {
        get
        {
            var nodes = _registry.GetAll();
            return nodes.Count > 0 && nodes.Any(n => n.Enabled);
        }
    }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Select the first enabled node (MVP: single-node routing)
        var node = _registry.GetAll().FirstOrDefault(n => n.Enabled);
        if (node is null)
        {
            _logger.LogWarning("No enabled local LLM tunnel node available.");
            return string.Empty;
        }

        // Determine model to use
        var model = _options.DefaultModel
            ?? node.Models.FirstOrDefault()
            ?? node.AdvertisedModels.FirstOrDefault()
            ?? "llama3.2:3b";

        // Build signed request
        var nonce = LocalLlmTunnelCrypto.ToBase64Url(RandomNumberGenerator.GetBytes(16));
        var now = DateTime.UtcNow;
        var signedReq = new LlmSignedInferenceRequest
        {
            RequestId = "llmreq_" + Guid.NewGuid().ToString("N")[..12],
            TenantId = "local",
            NodeId = node.NodeId,
            KeyId = node.KeyId,
            Nonce = nonce,
            IssuedAt = now,
            ExpiresAt = now.AddSeconds(30),
            Payload = new LlmTunnelCompletionRequest
            {
                Model = model,
                Messages = [new LlmTunnelMessage { Role = "user", Content = request.Prompt }],
                Temperature = request.Temperature,
                MaxTokens = request.MaxTokens,
                TimeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : _options.RequestTimeoutMs
            },
            Signature = ""
        };

        // Sign: decode shared secret from base64url
        var secret = LocalLlmTunnelCrypto.FromBase64Url(node.ControllerSharedSecret);
        signedReq.Signature = _crypto.SignRequest(signedReq, secret);

        // Send to agent
        var completeUrl = node.TunnelUrl.TrimEnd('/') + "/api/v1/llm-tunnel/complete";
        using var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromMilliseconds(
            request.TimeoutMs > 0 ? request.TimeoutMs : _options.RequestTimeoutMs);

        LlmSignedInferenceResponse? response;
        try
        {
            var httpResp = await client.PostAsJsonAsync(completeUrl, signedReq,
                TunnelJsonContext.Default.LlmSignedInferenceRequest, ct);
            if (!httpResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Tunnel agent returned {StatusCode} for node {NodeId}.",
                    (int)httpResp.StatusCode, node.NodeId);
                IncrementFailure(node);
                return string.Empty;
            }

            response = await httpResp.Content.ReadFromJsonAsync(
                TunnelJsonContext.Default.LlmSignedInferenceResponse,
                cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning("Tunnel agent request failed for node {NodeId}: {Message}",
                node.NodeId, ex.Message);
            IncrementFailure(node);
            return string.Empty;
        }

        if (response is null)
        {
            _logger.LogWarning("Null response from tunnel agent node {NodeId}.", node.NodeId);
            return string.Empty;
        }

        // Verify response signature
        if (!_crypto.VerifyResponse(response, secret))
        {
            _logger.LogWarning("Response signature verification failed for node {NodeId}.", node.NodeId);
            return string.Empty;
        }

        // Update last seen
        UpdateLastSeen(node);
        return response.Content;
    }

    private void IncrementFailure(LlmNodeDescriptor node)
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
            LastSeenAt = node.LastSeenAt,
            Enabled = node.Enabled,
            QueueDepth = node.QueueDepth,
            FailureCount = node.FailureCount + 1
        };
        _registry.Replace(updated);
    }

    private void UpdateLastSeen(LlmNodeDescriptor node)
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
        _registry.Replace(updated);
    }
}
