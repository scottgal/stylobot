namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Single source of truth for LLM default configuration.
///     All consumers (Ollama, Cloud, ShapeBuilder, Clustering, etc.)
///     should reference these constants rather than hardcoding model names.
/// </summary>
public static class LlmDefaults
{
    /// <summary>
    ///     Default Ollama model for all StyloBot LLM tasks.
    ///     gemma4 - Google's efficient model, good classification, thinking-capable.
    /// </summary>
    public const string DefaultModel = "gemma4";

    /// <summary>Default Ollama endpoint.</summary>
    public const string DefaultEndpoint = "http://localhost:11434";

    /// <summary>Default temperature for classification (low for consistency).</summary>
    public const float DefaultTemperature = 0.1f;

    /// <summary>Default max tokens for classification responses.</summary>
    public const int DefaultMaxTokens = 150;

    /// <summary>Max tokens when thinking is enabled (thinking uses more tokens).</summary>
    public const int ThinkingMaxTokens = 500;

    /// <summary>Default timeout for LLM requests in milliseconds.</summary>
    public const int DefaultTimeoutMs = 15000;

    /// <summary>Default number of CPU threads for Ollama inference.</summary>
    public const int DefaultNumThreads = 4;
}
