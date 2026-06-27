namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>Output of one LLM cluster-naming call.</summary>
public sealed record ClusterNamingResult(string Name, string? Description);
