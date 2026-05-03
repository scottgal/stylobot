using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Llm.Parsing;
using Mostlylucid.BotDetection.Llm.Prompts;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Services;

/// <summary>
///     Uses ILlmProvider + prompt builder + parser for bot/human classification.
///     Supports thinking-aware models (gemma4, qwen3, etc.) when configured.
/// </summary>
public class LlmClassificationService
{
    private readonly ILlmProvider _provider;
    private readonly ILogger<LlmClassificationService> _logger;
    private readonly BotDetectionOptions _options;

    public LlmClassificationService(
        ILlmProvider provider,
        ILogger<LlmClassificationService> logger,
        Microsoft.Extensions.Options.IOptions<BotDetectionOptions> options)
    {
        _provider = provider;
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    ///     Classify a pre-built request info string as bot/human.
    /// </summary>
    public async Task<DetectorResult> ClassifyAsync(string preBuiltRequestInfo, CancellationToken ct = default)
    {
        var result = new DetectorResult();

        if (!_provider.IsReady)
        {
            _logger.LogDebug("LLM provider not ready, skipping classification");
            return result;
        }

        var enableThinking = _options.AiDetection.Ollama.EnableThinking;

        try
        {
            var prompt = ClassificationPromptBuilder.Build(
                preBuiltRequestInfo,
                _options.AiDetection.Ollama.CustomPrompt);

            var request = new LlmRequest
            {
                Prompt = prompt,
                Temperature = LlmDefaults.DefaultTemperature,
                MaxTokens = enableThinking ? LlmDefaults.ThinkingMaxTokens : LlmDefaults.DefaultMaxTokens,
                TimeoutMs = _options.AiDetection.TimeoutMs,
                EnableThinking = enableThinking
            };

            var response = await _provider.CompleteWithThinkingAsync(request, ct);

            if (string.IsNullOrWhiteSpace(response.Content))
            {
                _logger.LogWarning("LLM returned empty response for classification");
                return result;
            }

            if (!string.IsNullOrWhiteSpace(response.Thinking))
                _logger.LogDebug("LLM thinking: {Thinking}", response.Thinking.Length > 200
                    ? response.Thinking[..200] + "..."
                    : response.Thinking);

            var analysis = LlmResponseParser.ParseClassification(response.Content);
            if (analysis == null || analysis.Reasoning == "Analysis failed")
                return result;

            // Enrich reasoning with thinking summary if available
            var reasoning = analysis.Reasoning;
            if (!string.IsNullOrWhiteSpace(response.Thinking))
                reasoning = $"{reasoning} [thinking: {TruncateThinking(response.Thinking)}]";

            if (analysis.IsBot)
            {
                result.Confidence = analysis.Confidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "LLM Analysis",
                    Detail = reasoning,
                    ConfidenceImpact = analysis.Confidence
                });
                result.BotType = analysis.BotType;
            }
            else
            {
                result.Confidence = 1.0 - analysis.Confidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "LLM Analysis",
                    Detail = $"LLM classified as human: {reasoning}",
                    ConfidenceImpact = -analysis.Confidence
                });
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLM classification timed out");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM classification failed");
        }

        return result;
    }

    private static string TruncateThinking(string thinking)
    {
        const int maxLen = 100;
        return thinking.Length <= maxLen ? thinking : thinking[..maxLen] + "...";
    }
}
