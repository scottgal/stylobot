using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Llm.Parsing;
using Mostlylucid.BotDetection.Llm.Prompts;

namespace Mostlylucid.BotDetection.Llm.Services;

/// <summary>
///     Uses ILlmProvider to generate plain English score change narratives.
/// </summary>
public class LlmScoreNarrativeService : IScoreNarrativeService
{
    private readonly ILlmProvider _provider;
    private readonly ILogger<LlmScoreNarrativeService> _logger;

    public LlmScoreNarrativeService(
        ILlmProvider provider,
        ILogger<LlmScoreNarrativeService> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    public async Task<string?> GenerateNarrativeAsync(
        string signature,
        double previousScore,
        double newScore,
        IReadOnlyDictionary<string, object?>? signals = null,
        CancellationToken ct = default)
    {
        if (!_provider.IsReady)
            return null;

        try
        {
            var prompt = ScoreNarrativePromptBuilder.Build(signature, previousScore, newScore, signals);

            var response = await _provider.CompleteAsync(new LlmRequest
            {
                Prompt = prompt,
                Temperature = 0.3f,
                MaxTokens = 100,
                TimeoutMs = 10000
            }, ct);

            if (string.IsNullOrWhiteSpace(response))
                return null;

            var cleaned = LlmResponseParser.StripMarkdownCodeFences(response);
            var jsonStr = ExtractJsonObject(cleaned);
            if (jsonStr == null) return cleaned.Trim(); // fallback: return raw text

            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                if (doc.RootElement.TryGetProperty("narrative", out var narrativeProp))
                    return narrativeProp.GetString()?.Trim();
            }
            catch (JsonException)
            {
                // If JSON parsing fails, return the cleaned response
            }

            return cleaned.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Score narrative generation failed for {Signature}", signature[..Math.Min(8, signature.Length)]);
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            return text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        return null;
    }
}
