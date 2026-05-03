using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Llm.Cloud;

/// <summary>
///     LLM provider for Google Gemini API.
///     Uses API key in query string, different request/response format.
/// </summary>
public sealed class GeminiLlmProvider : ILlmProvider
{
    private readonly HttpClient _http;
    private readonly CloudLlmOptions _options;
    private readonly string _model;
    private readonly string _baseUrl;
    private readonly ILogger<GeminiLlmProvider> _logger;

    public GeminiLlmProvider(
        IHttpClientFactory httpFactory,
        IOptions<CloudLlmOptions> options,
        ILogger<GeminiLlmProvider> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("stylobot-llm");

        var (baseUrl, model, _) = ProviderPresets.Resolve(_options);
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsReady => !string.IsNullOrEmpty(_options.ApiKey);

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!IsReady) return string.Empty;

        var url = $"{_baseUrl}/v1beta/models/{_model}:generateContent?key={_options.ApiKey}";

        var body = new GeminiRequest
        {
            SystemInstruction = new GeminiContent
            {
                Parts = [new() { Text = _options.SystemPrompt }]
            },
            Contents = [new() { Parts = [new() { Text = request.Prompt }] }],
            GenerationConfig = new()
            {
                Temperature = request.Temperature,
                MaxOutputTokens = request.MaxTokens > 0 ? request.MaxTokens : _options.MaxTokens
            }
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[gemini] {Status}: {Error}", resp.StatusCode, err.Length > 300 ? err[..300] : err);
                return string.Empty;
            }

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            var payload = JsonSerializer.Deserialize<GeminiResponse>(respJson, JsonOpts);
            return payload?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text?.Trim() ?? string.Empty;
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[gemini] Request failed");
            return string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal sealed record GeminiRequest
    {
        public GeminiContent? SystemInstruction { get; init; }
        public required List<GeminiContent> Contents { get; init; }
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    internal sealed record GeminiContent
    {
        public List<GeminiPart>? Parts { get; init; }
    }

    internal sealed record GeminiPart
    {
        public required string Text { get; init; }
    }

    internal sealed record GeminiGenerationConfig
    {
        public double Temperature { get; init; }
        public int MaxOutputTokens { get; init; }
    }

    internal sealed record GeminiResponse
    {
        public List<GeminiCandidate>? Candidates { get; init; }
    }

    internal sealed record GeminiCandidate
    {
        public GeminiContent? Content { get; init; }
    }
}
