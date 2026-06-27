namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>Output of one LLM signature-naming call.</summary>
public sealed record SignatureNamingResult(string Name, string? Description);
