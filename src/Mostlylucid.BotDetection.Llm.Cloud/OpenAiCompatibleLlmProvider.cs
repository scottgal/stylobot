using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Cloud;

/// <summary>
///     LLM provider for any OpenAI-compatible Chat Completions API.
///     Works with: OpenAI, Groq, Mistral, DeepSeek, Together, Fireworks,
///     OpenRouter, Azure OpenAI, vLLM, LM Studio, text-generation-webui.
/// </summary>
public sealed class OpenAiCompatibleLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly CloudLlmOptions _options;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly AuthStyle _auth;
    private readonly ILogger<OpenAiCompatibleLlmProvider> _logger;

    public OpenAiCompatibleLlmProvider(
        IHttpClientFactory httpFactory,
        IOptions<CloudLlmOptions> options,
        ILogger<OpenAiCompatibleLlmProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("stylobot-llm");

        var (baseUrl, model, auth) = ProviderPresets.Resolve(_options);
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _auth = auth;

        _http.BaseAddress = new Uri(_baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsReady => !string.IsNullOrEmpty(_baseUrl) &&
                           (_auth == AuthStyle.None || !string.IsNullOrEmpty(_options.ApiKey));

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!IsReady)
        {
            _logger.LogDebug("LLM provider not ready (no API key or base URL)");
            return string.Empty;
        }

        // Build request path
        string path;
        if (_auth == AuthStyle.AzureApiKey && !string.IsNullOrEmpty(_options.AzureDeployment))
            path = $"/openai/deployments/{_options.AzureDeployment}/chat/completions?api-version={_options.AzureApiVersion}";
        else
            path = "/v1/chat/completions";

        var body = new ChatRequest
        {
            Model = _model,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : _options.MaxTokens,
            Messages =
            [
                new() { Role = "system", Content = _options.SystemPrompt },
                new() { Role = "user", Content = request.Prompt }
            ]
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Set auth header based on provider style
        switch (_auth)
        {
            case AuthStyle.Bearer:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
                break;
            case AuthStyle.AzureApiKey:
                req.Headers.Add("api-key", _options.ApiKey);
                break;
            case AuthStyle.None:
                break;
        }

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[{Provider}] {Status}: {Error}",
                    _options.Provider, resp.StatusCode, Truncate(err, 300));
                return string.Empty;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<ChatResponse>(respJson, JsonOpts);
            return payload?.Choices?.FirstOrDefault()?.Message?.Content?.Trim() ?? string.Empty;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[{Provider}] Request timed out after {Timeout}s", _options.Provider, _options.TimeoutSeconds);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Provider}] LLM request failed", _options.Provider);
            return string.Empty;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record ChatRequest
    {
        public required string Model { get; init; }
        public required List<Message> Messages { get; init; }
        public double Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
    }

    internal sealed record Message
    {
        public required string Role { get; init; }
        public required string Content { get; init; }
    }

    internal sealed record ChatResponse
    {
        public List<Choice>? Choices { get; init; }
    }

    internal sealed record Choice
    {
        public Message? Message { get; init; }
    }
}
