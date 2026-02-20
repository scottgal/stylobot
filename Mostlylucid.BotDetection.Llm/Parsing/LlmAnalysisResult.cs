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
}
