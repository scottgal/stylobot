using System.Net;
using Mostlylucid.BotDetection.Llm.Tunnel;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

public class LocalLlmProviderProbeTests
{
    private static LocalLlmProviderProbe BuildProbe(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new LocalLlmProviderProbe(NullLogger<LocalLlmProviderProbe>.Instance, client);
    }

    [Fact]
    public async Task ProbeAsync_ValidOllamaResponse_ReturnsModels()
    {
        var json = """
            {
              "models": [
                {
                  "name": "llama3.2:3b",
                  "details": {
                    "family": "llama",
                    "parameter_size": "3.2B",
                    "quantization_level": "Q4_K_M"
                  }
                },
                {
                  "name": "qwen2.5:14b",
                  "details": {
                    "family": "qwen",
                    "parameter_size": "14B",
                    "quantization_level": "Q4_K_M"
                  }
                }
              ]
            }
            """;

        var handler = new FakeHandler(json);
        var probe = BuildProbe(handler);

        var result = await probe.ProbeAsync("http://127.0.0.1:11434");

        Assert.True(result.IsReady);
        Assert.NotNull(result.Inventory);
        Assert.Equal(2, result.Inventory!.Models.Count);
        Assert.Equal("llama3.2:3b", result.Inventory.Models[0].Id);
        Assert.Equal("llama", result.Inventory.Models[0].Family);
    }

    [Fact]
    public async Task ProbeAsync_AllowListFilters_ReturnsOnlyAllowed()
    {
        var json = """
            {
              "models": [
                { "name": "llama3.2:3b", "details": { "parameter_size": "3.2B" } },
                { "name": "qwen2.5:14b", "details": { "parameter_size": "14B" } }
              ]
            }
            """;

        var handler = new FakeHandler(json);
        var probe = BuildProbe(handler);

        var result = await probe.ProbeAsync("http://127.0.0.1:11434", ["llama3.2:3b"]);

        Assert.True(result.IsReady);
        Assert.Single(result.Inventory!.Models);
        Assert.Equal("llama3.2:3b", result.Inventory.Models[0].Id);
    }

    [Fact]
    public async Task ProbeAsync_MaxContextCaps_ContextLength()
    {
        var json = """
            {
              "models": [
                { "name": "llama3.2:3b", "details": { "parameter_size": "3.2B" } }
              ]
            }
            """;

        var handler = new FakeHandler(json);
        var probe = BuildProbe(handler);

        var result = await probe.ProbeAsync("http://127.0.0.1:11434", maxContextTokens: 2048);

        Assert.True(result.IsReady);
        Assert.Equal(2048, result.Inventory!.Models[0].ContextLength);
    }

    [Fact]
    public async Task ProbeAsync_OllamaUnavailable_ReturnsUnready()
    {
        var handler = new FakeHandler(HttpStatusCode.ServiceUnavailable, "");
        var probe = BuildProbe(handler);

        var result = await probe.ProbeAsync("http://127.0.0.1:11434");

        Assert.False(result.IsReady);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ProbeAsync_EmptyModels_ReturnsEmptyInventory()
    {
        var json = """{ "models": [] }""";
        var handler = new FakeHandler(json);
        var probe = BuildProbe(handler);

        var result = await probe.ProbeAsync("http://127.0.0.1:11434");

        Assert.True(result.IsReady);
        Assert.Empty(result.Inventory!.Models);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _content;
        private readonly HttpStatusCode _status;

        public FakeHandler(string content, HttpStatusCode status = HttpStatusCode.OK)
        {
            _content = content;
            _status = status;
        }

        public FakeHandler(HttpStatusCode status, string content)
        {
            _status = status;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_content,
                    System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
