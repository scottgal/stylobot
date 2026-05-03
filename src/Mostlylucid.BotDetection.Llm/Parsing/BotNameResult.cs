namespace Mostlylucid.BotDetection.Llm.Parsing;

/// <summary>
///     Result of bot name + description synthesis.
/// </summary>
public sealed record BotNameResult(string? Name, string? Description);
