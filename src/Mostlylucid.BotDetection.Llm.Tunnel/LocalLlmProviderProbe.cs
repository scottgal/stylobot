using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Llm.Tunnel;

/// <summary>
/// Probes a local Ollama instance to discover available models
/// and returns sanitized model metadata.
/// </summary>
public sealed class LocalLlmProviderProbe(
    ILogger<LocalLlmProviderProbe> logger,
    HttpClient? httpClient = null)
{
    /// <summary>
    /// Probe the Ollama instance and return filtered model inventory.
    /// </summary>
    /// <param name="ollamaBaseUrl">e.g. http://127.0.0.1:11434</param>
    /// <param name="allowedModels">If non-empty, only include these models.</param>
    /// <param name="maxContextTokens">Cap ContextLength to this value.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<LlmProbeResult> ProbeAsync(
        string ollamaBaseUrl,
        IReadOnlyList<string>? allowedModels = null,
        int maxContextTokens = 8192,
        CancellationToken ct = default)
    {
        // Use an injected client (for tests) or create a short-lived one (production).
        // Do not mutate a pooled IHttpClientFactory client.
        using var ownedClient = httpClient is null
            ? new HttpClient
            {
                BaseAddress = new Uri(ollamaBaseUrl.TrimEnd('/') + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            }
            : null;
        var client = ownedClient ?? httpClient!;
        if (ownedClient is null)
        {
            // For injected clients (tests), configure base address if not already set
            client.BaseAddress ??= new Uri(ollamaBaseUrl.TrimEnd('/') + "/");
        }

        string rawJson;
        try
        {
            rawJson = await client.GetStringAsync("api/tags", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            logger.LogWarning("Ollama probe failed at {Url}: {Message}", ollamaBaseUrl, ex.Message);
            return LlmProbeResult.Unready("Ollama unavailable: " + ex.Message);
        }

        // Parse manually so we don't need reflection-based JSON serialization (AOT-safe)
        using var doc = JsonDocument.Parse(rawJson);
        if (!doc.RootElement.TryGetProperty("models", out var modelsElement)
            || modelsElement.ValueKind != JsonValueKind.Array
            || modelsElement.GetArrayLength() == 0)
        {
            logger.LogWarning("Ollama at {Url} returned no models.", ollamaBaseUrl);
            return LlmProbeResult.Empty();
        }

        var allowSet = allowedModels is { Count: > 0 }
            ? new HashSet<string>(allowedModels, StringComparer.OrdinalIgnoreCase)
            : null;

        var models = new List<LlmNodeModelInfo>();
        foreach (var m in modelsElement.EnumerateArray())
        {
            var name = m.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            if (allowSet is not null && !allowSet.Contains(name)) continue;

            string? family = null, parameterSize = null, quantizationLevel = null;
            if (m.TryGetProperty("details", out var detailsEl))
            {
                family = detailsEl.TryGetProperty("family", out var f) ? f.GetString() : null;
                parameterSize = detailsEl.TryGetProperty("parameter_size", out var ps) ? ps.GetString() : null;
                quantizationLevel = detailsEl.TryGetProperty("quantization_level", out var ql) ? ql.GetString() : null;
            }

            models.Add(new LlmNodeModelInfo
            {
                Id = name,
                Family = family,
                ParameterSize = parameterSize,
                Quantization = quantizationLevel,
                ContextLength = Math.Min(maxContextTokens, GuessContextLength(parameterSize)),
                Allowed = true,
                SupportsStreaming = true
            });
        }

        return new LlmProbeResult
        {
            IsReady = true,
            Inventory = new LlmNodeModelInventory
            {
                Provider = "ollama",
                Models = models
            }
        };
    }

    /// Estimate a reasonable context length from the parameter size string.
    private static int GuessContextLength(string? paramSize)
    {
        if (paramSize is null) return 4096;
        return paramSize.ToLowerInvariant() switch
        {
            var s when s.Contains("70b") || s.Contains("72b") => 8192,
            var s when s.Contains("34b") || s.Contains("30b") => 8192,
            var s when s.Contains("14b") || s.Contains("13b") => 8192,
            var s when s.Contains("8b")  || s.Contains("7b")  => 8192,
            _ => 4096
        };
    }

}

/// <summary>Result of probing the local LLM provider.</summary>
public sealed class LlmProbeResult
{
    public bool IsReady { get; set; }
    public string? Error { get; set; }
    public LlmNodeModelInventory? Inventory { get; set; }

    public static LlmProbeResult Unready(string error) => new() { IsReady = false, Error = error };
    public static LlmProbeResult Empty() => new() { IsReady = true, Inventory = new LlmNodeModelInventory { Provider = "ollama" } };
}
