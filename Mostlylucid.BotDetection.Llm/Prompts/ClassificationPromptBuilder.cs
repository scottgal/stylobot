namespace Mostlylucid.BotDetection.Llm.Prompts;

/// <summary>
///     Builds the bot/human classification prompt from pre-built request info.
///     Extracted from LlmDetector.BuildRequestInfo() and OllamaOptions.DefaultPrompt.
/// </summary>
public static class ClassificationPromptBuilder
{
    /// <summary>
    ///     Default compact prompt template for bot detection.
    ///     Optimized for minimal token usage with small models.
    /// </summary>
    public const string DefaultPrompt = @"Bot detector. JSON only.
{REQUEST_INFO}
RULES(priority order):
1. prob<0.3->human (trust H model)
2. prob>0.7->bot (trust H model)
3. ua~bot/crawler/spider/scraper->bot
4. ua~curl/wget/python/headless/sqlmap->bot
5. referer+lang+cookies->human
6. Chrome/Firefox/Safari+hdrs>=10->human
7. unsure->human,conf=0.3
TYPE:scraper|searchengine|monitor|malicious|social|good|unknown
{""isBot"":false,""confidence"":0.8,""reasoning"":""..."",""botType"":""unknown""}";

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
