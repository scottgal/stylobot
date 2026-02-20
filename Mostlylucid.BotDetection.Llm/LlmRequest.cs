namespace Mostlylucid.BotDetection.Llm;

/// <summary>
///     A request to send to an LLM provider.
/// </summary>
public sealed record LlmRequest
{
    public required string Prompt { get; init; }
    public float Temperature { get; init; } = 0.1f;
    public int MaxTokens { get; init; } = 150;
    public int TimeoutMs { get; init; } = 15000;
}
