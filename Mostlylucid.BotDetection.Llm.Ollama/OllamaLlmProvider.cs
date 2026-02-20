using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace Mostlylucid.BotDetection.Llm.Ollama;

/// <summary>
///     ILlmProvider backed by an external Ollama HTTP server.
///     Extracted from LlmDetector.AnalyzeWithLlm().
/// </summary>
public class OllamaLlmProvider : ILlmProvider
{
    private readonly ILogger<OllamaLlmProvider> _logger;
    private readonly OllamaProviderOptions _options;
    private volatile bool _ready;

    public OllamaLlmProvider(
        ILogger<OllamaLlmProvider> logger,
        IOptions<OllamaProviderOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsReady => _ready || !string.IsNullOrEmpty(_options.Endpoint);

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _ready = !string.IsNullOrEmpty(_options.Endpoint);
        return Task.CompletedTask;
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.Endpoint))
            return string.Empty;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(request.TimeoutMs);

        try
        {
            var ollama = new OllamaApiClient(_options.Endpoint)
            {
                SelectedModel = _options.Model
            };

            var chat = new Chat(ollama)
            {
                Options = new OllamaSharp.Models.RequestOptions
                {
                    NumThread = _options.NumThreads
                },
                Think = false
            };

            var responseBuilder = new StringBuilder();
            await foreach (var token in chat.SendAsync(request.Prompt, cts.Token))
                responseBuilder.Append(token);

            var response = responseBuilder.ToString();

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("Ollama returned empty response for model '{Model}'", _options.Model);
                return string.Empty;
            }

            // Check for Ollama error responses
            if (response.Contains("error", StringComparison.OrdinalIgnoreCase) &&
                (response.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                 response.Contains("failed", StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogError("Ollama returned an error: {Response}",
                    response.Length > 500 ? response[..500] + "..." : response);
                return string.Empty;
            }

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ollama request timed out after {Timeout}ms", request.TimeoutMs);
            return string.Empty;
        }
        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogError("Ollama model '{Model}' not found at {Endpoint}. Run 'ollama pull {Model}' to download it",
                _options.Model, _options.Endpoint, _options.Model);
            return string.Empty;
        }
        catch (HttpRequestException httpEx)
        {
            _logger.LogError(httpEx, "Ollama HTTP error ({StatusCode}) at {Endpoint}",
                (int?)httpEx.StatusCode ?? 0, _options.Endpoint);
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama completion failed: {Message}", ex.Message);
            return string.Empty;
        }
    }
}
