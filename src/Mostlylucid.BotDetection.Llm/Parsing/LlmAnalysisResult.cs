using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Parsing;

/// <summary>
///     Result of LLM bot/human classification analysis.
/// </summary>
public class LlmAnalysisResult
{
    public bool IsBot { get; set; }
    public double Confidence { get; set; }
    public required string Reasoning { get; set; }
    public BotType BotType { get; set; }
    public string? Pattern { get; set; }

    /// <summary>Short descriptive name for the bot (e.g., "Python API Scraper").</summary>
    public string? BotName { get; set; }

    /// <summary>LLM requests more detectors run (evidence was ambiguous).</summary>
    public bool Escalate { get; set; }
}
