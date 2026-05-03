using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm;

/// <summary>
///     A request to send to an LLM provider.
/// </summary>
public sealed record LlmRequest
{
    public required string Prompt { get; init; }
    public float Temperature { get; init; } = LlmDefaults.DefaultTemperature;
    public int MaxTokens { get; init; } = LlmDefaults.DefaultMaxTokens;
    public int TimeoutMs { get; init; } = LlmDefaults.DefaultTimeoutMs;

    /// <summary>
    ///     Enable thinking/chain-of-thought mode for models that support it (gemma4, qwen3, etc.).
    ///     When enabled, the model reasons internally before producing the final answer.
    ///     The thinking content is returned separately and does not pollute the classification JSON.
    /// </summary>
    public bool EnableThinking { get; init; }
}

/// <summary>
///     Response from an LLM provider, including optional thinking content.
/// </summary>
public sealed record LlmResponse
{
    /// <summary>The final content (classification JSON).</summary>
    public required string Content { get; init; }

    /// <summary>
    ///     The model's chain-of-thought reasoning, if thinking was enabled.
    ///     Null when thinking is disabled or the model doesn't support it.
    /// </summary>
    public string? Thinking { get; init; }
}
