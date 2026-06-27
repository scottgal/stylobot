namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>Output of one LLM fingerprint-naming call. Written through
/// <see cref="Identity.IFingerprintStore.UpdateLlmNameAsync"/> by the writeback.</summary>
public sealed record FingerprintNamingResult(string Name, string? Description);
