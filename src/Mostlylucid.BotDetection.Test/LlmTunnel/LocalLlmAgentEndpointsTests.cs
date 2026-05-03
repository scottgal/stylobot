using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Llm;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Xunit;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

public class LocalLlmAgentEndpointsTests : IAsyncDisposable
{
    private readonly byte[] _secret = LocalLlmTunnelCrypto.GenerateSigningSecret();
    private const string NodeId = "llmn_01hw";
    private const string KeyId = "k_01hw";

    // Track apps for disposal
    private readonly List<WebApplication> _apps = new();

    private HttpClient BuildClient(ILlmProvider llmProvider)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var crypto = new LocalLlmTunnelCrypto();
        var ctx = new LocalLlmAgentContext
        {
            NodeId = NodeId,
            KeyId = KeyId,
            SigningSecret = _secret,
            Provider = "ollama",
            MaxConcurrency = 2,
            MaxContext = 8192,
            ModelInventory = new LlmNodeModelInventory
            {
                Provider = "ollama",
                Models = [new LlmNodeModelInfo { Id = "llama3.2:3b", Allowed = true }]
            }
        };

        builder.Services.AddSingleton(crypto);
        builder.Services.AddSingleton(ctx);
        builder.Services.AddSingleton(llmProvider);

        var app = builder.Build();
        app.MapLocalLlmAgentEndpoints();
        app.StartAsync().GetAwaiter().GetResult();

        _apps.Add(app);
        return app.GetTestClient();
    }

    private LlmSignedInferenceRequest MakeSignedRequest(string model = "llama3.2:3b")
    {
        var req = new LlmSignedInferenceRequest
        {
            RequestId = "llmreq_01hw",
            TenantId = "tenant_01",
            NodeId = NodeId,
            KeyId = KeyId,
            Nonce = LocalLlmTunnelCrypto.ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
            Payload = new LlmTunnelCompletionRequest
            {
                Model = model,
                Messages = [new LlmTunnelMessage { Role = "user", Content = "classify" }],
                MaxTokens = 512
            },
            Signature = ""
        };
        var crypto = new LocalLlmTunnelCrypto();
        req.Signature = crypto.SignRequest(req, _secret);
        return req;
    }

    [Fact]
    public async Task GetHealth_ReturnsReady()
    {
        var client = BuildClient(new FakeLlmProvider("{}"));
        var resp = await client.GetAsync("/api/v1/llm-tunnel/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var health = await resp.Content.ReadFromJsonAsync<LlmTunnelHealthResponse>();
        Assert.Equal("ready", health!.Status);
        Assert.Equal(NodeId, health.NodeId);
    }

    [Fact]
    public async Task GetModels_ReturnsInventory()
    {
        var client = BuildClient(new FakeLlmProvider("{}"));
        var resp = await client.GetAsync("/api/v1/llm-tunnel/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var inv = await resp.Content.ReadFromJsonAsync<LlmNodeModelInventory>();
        Assert.Single(inv!.Models);
    }

    [Fact]
    public async Task PostComplete_ValidRequest_ReturnsSignedResponse()
    {
        var client = BuildClient(new FakeLlmProvider("{\"isBot\":true}"));
        var req = MakeSignedRequest();

        var resp = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<LlmSignedInferenceResponse>();
        Assert.Equal(req.RequestId, body!.RequestId);
        Assert.NotEmpty(body.Signature);
    }

    [Fact]
    public async Task PostComplete_ExpiredRequest_Returns401()
    {
        var client = BuildClient(new FakeLlmProvider("{}"));
        // Build a request with past expiry and sign it
        var req = new LlmSignedInferenceRequest
        {
            RequestId = "llmreq_exp",
            TenantId = "tenant_01",
            NodeId = NodeId,
            KeyId = KeyId,
            Nonce = LocalLlmTunnelCrypto.ToBase64Url(RandomNumberGenerator.GetBytes(16)),
            IssuedAt = DateTime.UtcNow.AddSeconds(-60),
            ExpiresAt = DateTime.UtcNow.AddSeconds(-10),
            Payload = new LlmTunnelCompletionRequest
            {
                Model = "llama3.2:3b",
                Messages = [new LlmTunnelMessage { Role = "user", Content = "classify" }],
                MaxTokens = 512
            },
            Signature = ""
        };
        var crypto = new LocalLlmTunnelCrypto();
        req.Signature = crypto.SignRequest(req, _secret);

        var resp = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PostComplete_BadSignature_Returns401()
    {
        var client = BuildClient(new FakeLlmProvider("{}"));
        var req = MakeSignedRequest();
        req.Signature = "badsig";

        var resp = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task PostComplete_DisallowedModel_Returns422()
    {
        var client = BuildClient(new FakeLlmProvider("{}"));
        var req = MakeSignedRequest("gpt-4");

        var resp = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task PostComplete_ReplayedNonce_Returns401()
    {
        var client = BuildClient(new FakeLlmProvider("{\"isBot\":false}"));
        var req = MakeSignedRequest();

        // First request succeeds
        var first = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Replay the same nonce — must be rejected
        var second = await client.PostAsJsonAsync("/api/v1/llm-tunnel/complete", req);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeLlmProvider(string result) : ILlmProvider
    {
        public bool IsReady => true;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
            => Task.FromResult(result);
    }
}
