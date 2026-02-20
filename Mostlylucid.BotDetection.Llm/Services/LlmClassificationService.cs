using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Llm.Parsing;
using Mostlylucid.BotDetection.Llm.Prompts;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Services;

/// <summary>
///     Uses ILlmProvider + prompt builder + parser for bot/human classification.
///     Replaces the old LlmDetector's AnalyzeWithLlm + DetectFromSnapshotAsync.
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

        try
        {
            var prompt = ClassificationPromptBuilder.Build(
                preBuiltRequestInfo,
                _options.AiDetection.Ollama.CustomPrompt);

            var response = await _provider.CompleteAsync(new LlmRequest
            {
                Prompt = prompt,
                Temperature = 0.1f,
                MaxTokens = 150,
                TimeoutMs = _options.AiDetection.TimeoutMs
            }, ct);

            if (string.IsNullOrWhiteSpace(response))
            {
                _logger.LogWarning("LLM returned empty response for classification");
                return result;
            }

            var analysis = LlmResponseParser.ParseClassification(response);
            if (analysis == null || analysis.Reasoning == "Analysis failed")
                return result;

            if (analysis.IsBot)
            {
                result.Confidence = analysis.Confidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "LLM Analysis",
                    Detail = analysis.Reasoning,
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
                    Detail = $"LLM classified as human: {analysis.Reasoning}",
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
}
