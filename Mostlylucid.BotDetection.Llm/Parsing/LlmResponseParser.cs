using System.Text.Json;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Parsing;

/// <summary>
///     Parses raw LLM text responses into typed results.
///     Handles markdown stripping, JSON extraction, and partial JSON recovery.
/// </summary>
public static partial class LlmResponseParser
{
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Parse a classification response from the LLM.
    ///     Tries: direct JSON → JSON extraction → partial regex fallback.
    /// </summary>
    public static LlmAnalysisResult? ParseClassification(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var cleaned = StripMarkdownCodeFences(response);

        // Try direct JSON parse
        var json = TryDeserialize(cleaned);
        if (json != null) return ToAnalysis(json);

        // Try extracting JSON object from surrounding text
        var extracted = ExtractJsonObject(cleaned);
        if (extracted != null)
        {
            json = TryDeserialize(extracted);
            if (json != null) return ToAnalysis(json);
        }

        // Fallback: partial regex extraction for truncated responses
        return TryExtractPartialJson(cleaned);
    }

    /// <summary>
    ///     Parse a bot name/description response from the LLM.
    /// </summary>
    public static BotNameResult ParseBotName(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new BotNameResult(null, null);

        var cleaned = StripMarkdownCodeFences(response);
        var jsonStr = ExtractJsonObject(cleaned);
        if (jsonStr == null)
            return new BotNameResult(null, null);

        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var nameProp)
                ? nameProp.GetString()?.Trim()
                : null;

            var description = root.TryGetProperty("description", out var descProp)
                ? descProp.GetString()?.Trim()
                : null;

            return new BotNameResult(name, description);
        }
        catch
        {
            return new BotNameResult(null, null);
        }
    }

    /// <summary>
    ///     Strips markdown code fences from LLM response.
    ///     Handles: ```json ... ```, ``` ... ```, and variations.
    /// </summary>
    public static string StripMarkdownCodeFences(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return response;

        var trimmed = response.Trim();

        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline > 0)
                trimmed = trimmed[(firstNewline + 1)..];
            else
                trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3];

        return trimmed.Trim();
    }

    public static BotType ParseBotType(string? botType)
    {
        if (string.IsNullOrEmpty(botType))
            return BotType.Unknown;

        return botType.ToLowerInvariant() switch
        {
            "scraper" => BotType.Scraper,
            "searchengine" => BotType.SearchEngine,
            "monitor" or "monitoring" => BotType.MonitoringBot,
            "malicious" => BotType.MaliciousBot,
            "social" or "socialmedia" => BotType.SocialMediaBot,
            "good" or "verified" => BotType.GoodBot,
            _ => BotType.Unknown
        };
    }

    private static string? ExtractJsonObject(string text)
    {
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            return text.Substring(jsonStart, jsonEnd - jsonStart + 1);
        return null;
    }

    private static ClassificationJson? TryDeserialize(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ClassificationJson>(text, CaseInsensitiveJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmAnalysisResult ToAnalysis(ClassificationJson json)
    {
        return new LlmAnalysisResult
        {
            IsBot = json.IsBot,
            Confidence = Math.Clamp(json.Confidence, 0.0, 1.0),
            Reasoning = json.Reasoning ?? "No reasoning provided",
            BotType = ParseBotType(json.BotType),
            Pattern = json.Pattern
        };
    }

    private static LlmAnalysisResult? TryExtractPartialJson(string text)
    {
        var isBotMatch = IsBotRegex().Match(text);
        if (!isBotMatch.Success)
            return null;

        bool isBot;
        if (isBotMatch.Groups[2].Success)
            isBot = isBotMatch.Groups[2].Value != "0";
        else
            isBot = isBotMatch.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

        var confidenceMatch = ConfidenceRegex().Match(text);
        var confidence = isBot ? 0.7 : 0.3;
        if (confidenceMatch.Success && double.TryParse(confidenceMatch.Groups[1].Value, out var parsedConf))
            confidence = parsedConf > 1 ? parsedConf / 100.0 : parsedConf;

        var reasoningMatch = ReasoningRegex().Match(text);
        var reasoning = reasoningMatch.Success
            ? reasoningMatch.Groups[1].Value + " (partial response)"
            : "Partial response from LLM";

        return new LlmAnalysisResult
        {
            IsBot = isBot,
            Confidence = confidence,
            Reasoning = reasoning,
            BotType = BotType.Unknown
        };
    }

    [GeneratedRegex(@"""?is_?bot""?\s*:\s*(true|false|""?(\d+)""?)", RegexOptions.IgnoreCase)]
    private static partial Regex IsBotRegex();

    [GeneratedRegex(@"""?confidence""?\s*:\s*""?(\d+\.?\d*)""?", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(@"""?reasoning""?\s*:\s*""([^""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReasoningRegex();

    private class ClassificationJson
    {
        public bool IsBot { get; set; }
        public double Confidence { get; set; }
        public string? Reasoning { get; set; }
        public string? BotType { get; set; }
        public string? Pattern { get; set; }
    }
}
