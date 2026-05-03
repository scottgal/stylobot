using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Cloud;

/// <summary>
///     LLM provider for the Anthropic Messages API.
///     Uses x-api-key auth and anthropic-version header.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly CloudLlmOptions _options;
    private readonly string _model;
    private readonly ILogger<AnthropicLlmProvider> _logger;

    public AnthropicLlmProvider(
        IHttpClientFactory httpFactory,
        IOptions<CloudLlmOptions> options,
        ILogger<AnthropicLlmProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("stylobot-llm");

        var (baseUrl, model, _) = ProviderPresets.Resolve(_options);
        _model = model;
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsReady => !string.IsNullOrEmpty(_options.ApiKey);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!IsReady) return string.Empty;

        var body = new AnthropicRequest
        {
            Model = _model,
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : _options.MaxTokens,
            System = _options.SystemPrompt,
            Messages = [new() { Role = "user", Content = request.Prompt }]
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("x-api-key", _options.ApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[anthropic] {Status}: {Error}", resp.StatusCode, err.Length > 300 ? err[..300] : err);
                return string.Empty;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<AnthropicResponse>(respJson, JsonOpts);
            return payload?.Content?.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[anthropic] Request failed");
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record AnthropicRequest
    {
        public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        public string? System { get; init; }
        public required List<AnthropicMessage> Messages { get; init; }
    }

    internal sealed record AnthropicMessage
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    internal sealed record AnthropicResponse
    {
        public List<ContentBlock>? Content { get; init; }
    }

    internal sealed record ContentBlock
    {
        public string? Type { get; init; }
        public string? Text { get; init; }
    }
}
