namespace Mostlylucid.BotDetection.Llm.Prompts;

/// <summary>
///     Builds the bot/human classification prompt from pre-built request info.
///     Extracted from LlmDetector.BuildRequestInfo() and OllamaOptions.DefaultPrompt.
/// </summary>
public static class ClassificationPromptBuilder
{
    /// <summary>
    ///     Default prompt template. Presents detector findings as plain facts.
    ///     The LLM evaluates evidence and classifies - no rules or opinions from us.
    /// </summary>
    public const string DefaultPrompt = @"You are a bot detection analyst. Below are the factual findings from automated detectors examining an HTTP request. Evaluate the evidence and classify this request.

{REQUEST_INFO}
Based on these findings, respond with JSON only:
{""isBot"":true/false,""confidence"":0.0-1.0,""botType"":""scraper|searchengine|monitor|malicious|social|good|unknown"",""name"":""short descriptive name"",""reasoning"":""one sentence explaining your conclusion"",""escalate"":false}

Set escalate=true if the evidence is ambiguous and you want more detectors to run.";

    /// <summary>
    ///     Build the classification prompt from pre-built request info.
    /// </summary>
    public static string Build(string preBuiltRequestInfo, string? customPrompt = null)
    {
        var template = !string.IsNullOrEmpty(customPrompt)
            ? customPrompt
            : DefaultPrompt;

        return template.Replace("{REQUEST_INFO}", preBuiltRequestInfo);
    }
}
