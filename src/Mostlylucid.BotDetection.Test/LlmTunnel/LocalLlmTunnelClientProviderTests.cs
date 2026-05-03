using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Llm;
using Mostlylucid.BotDetection.Llm.Tunnel;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

public class LocalLlmTunnelClientProviderTests
{
    private static readonly byte[] SharedSecret = LocalLlmTunnelCrypto.GenerateSigningSecret();

    private static LlmNodeDescriptor MakeNode() => new()
    {
        NodeId = "llmn_test",
        Name = "test-node",
        TunnelUrl = "https://test.trycloudflare.com",
        TunnelKind = "cloudflare-quick",
        Provider = "ollama",
        Models = ["llama3.2:3b"],
        AdvertisedModels = ["llama3.2:3b"],
        KeyId = "k_test",
        ControllerSharedSecret = LocalLlmTunnelCrypto.ToBase64Url(SharedSecret),
        Enabled = true,
        LastSeenAt = DateTime.UtcNow
    };

    private LocalLlmTunnelClientProvider BuildProvider(HttpMessageHandler handler)
    {
        var registry = new InMemoryLlmNodeRegistry();
        registry.Register(MakeNode());

        var services = new ServiceCollection();
        services.AddHttpClient("stylobot-llm-tunnel-client")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var sp = services.BuildServiceProvider();

        return new LocalLlmTunnelClientProvider(
            registry,
            Options.Create(new LocalLlmTunnelOptions { DefaultModel = "llama3.2:3b" }),
            sp.GetRequiredService<IHttpClientFactory>(),
            new LocalLlmTunnelCrypto(),
            NullLogger<LocalLlmTunnelClientProvider>.Instance);
    }

    private HttpMessageHandler MakeAgentHandler(string content)
    {
        return new AgentSimulatorHandler(content, SharedSecret);
    }

    [Fact]
    public async Task CompleteAsync_ValidNode_ReturnsContent()
    {
        var provider = BuildProvider(MakeAgentHandler("{\"isBot\":true}"));
        var req = new LlmRequest { Prompt = "classify this", MaxTokens = 512 };

        var result = await provider.CompleteAsync(req);

        Assert.Equal("{\"isBot\":true}", result);
    }

    [Fact]
    public async Task CompleteAsync_NoNodes_ReturnsEmpty()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var services = new ServiceCollection();
        services.AddHttpClient("stylobot-llm-tunnel-client");
        var sp = services.BuildServiceProvider();

        var provider = new LocalLlmTunnelClientProvider(
            registry,
            Options.Create(new LocalLlmTunnelOptions()),
            sp.GetRequiredService<IHttpClientFactory>(),
            new LocalLlmTunnelCrypto(),
            NullLogger<LocalLlmTunnelClientProvider>.Instance);

        var result = await provider.CompleteAsync(new LlmRequest { Prompt = "test", MaxTokens = 100 });
        Assert.Empty(result);
    }

    [Fact]
    public async Task CompleteAsync_AgentReturns503_ReturnsEmpty()
    {
        var provider = BuildProvider(new FixedResponseHandler(HttpStatusCode.ServiceUnavailable, ""));
        var req = new LlmRequest { Prompt = "test", MaxTokens = 100 };
        var result = await provider.CompleteAsync(req);
        Assert.Empty(result);
    }

    [Fact]
    public void IsReady_WithEnabledNode_ReturnsTrue()
    {
        var provider = BuildProvider(new FixedResponseHandler(HttpStatusCode.OK, "{}"));
        Assert.True(provider.IsReady);
    }

    [Fact]
    public void IsReady_NoNodes_ReturnsFalse()
    {
        var registry = new InMemoryLlmNodeRegistry();
        var services = new ServiceCollection();
        services.AddHttpClient("stylobot-llm-tunnel-client");
        var sp = services.BuildServiceProvider();

        var provider = new LocalLlmTunnelClientProvider(
            registry,
            Options.Create(new LocalLlmTunnelOptions()),
            sp.GetRequiredService<IHttpClientFactory>(),
            new LocalLlmTunnelCrypto(),
            NullLogger<LocalLlmTunnelClientProvider>.Instance);

        Assert.False(provider.IsReady);
    }

    // Simulates the agent: reads the request, builds and signs a response
    private sealed class AgentSimulatorHandler(string content, byte[] secret) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var req = await request.Content!.ReadFromJsonAsync<LlmSignedInferenceRequest>(ct);
            var crypto = new LocalLlmTunnelCrypto();
            var resp = new LlmSignedInferenceResponse
            {
                RequestId = req!.RequestId,
                Model = req.Payload.Model,
                Content = content,
                LatencyMs = 100,
                Signature = ""
            };
            resp.Signature = crypto.SignResponse(resp, secret);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(resp)
            };
        }
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
