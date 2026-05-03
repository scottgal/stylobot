namespace Mostlylucid.BotDetection.Llm.Cloud;

/// <summary>
///     Unified configuration for cloud LLM providers.
///     Works with presets (openai, anthropic, groq, etc.) or custom endpoints.
/// </summary>
public sealed class CloudLlmOptions
{
    /// <summary>
    ///     Provider preset name: openai, anthropic, gemini, groq, mistral, deepseek,
    ///     together, fireworks, azure, ollama, llamasharp.
    ///     Determines default BaseUrl, Model, and auth style.
    /// </summary>
    public string Provider { get; set; } = "openai";

    /// <summary>API key. Also reads from STYLOBOT_LLM_KEY environment variable.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Base URL. Null = use preset default. Override for custom/self-hosted endpoints.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Model name. Null = use preset default.</summary>
    public string? Model { get; set; }

    /// <summary>Sampling temperature. Low values (0.1) for consistent classification.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Maximum response tokens. Bot classification responses are short.</summary>
    public int MaxTokens { get; set; } = 256;

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>System prompt prepended to every request.</summary>
    public string SystemPrompt { get; set; } =
        "You are an expert bot detection classifier. Given request signals, classify the " +
        "request as one of: Human, GoodBot, BadBot, Unknown. Reply with the classification " +
        "and a one-sentence reason.";

    /// <summary>Azure-specific: deployment name (replaces model in URL path).</summary>
    public string? AzureDeployment { get; set; }

    /// <summary>Azure-specific: API version.</summary>
    public string AzureApiVersion { get; set; } = "2024-10-21";
}
