using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Llm.Ollama;

/// <summary>
///     Configuration for the Ollama HTTP LLM provider.
/// </summary>
public class OllamaProviderOptions
{
    /// <summary>Ollama API endpoint URL.</summary>
    public string Endpoint { get; set; } = LlmDefaults.DefaultEndpoint;

    /// <summary>Ollama model to use.</summary>
    public string Model { get; set; } = LlmDefaults.DefaultModel;

    /// <summary>Number of CPU threads for Ollama inference.</summary>
    public int NumThreads { get; set; } = LlmDefaults.DefaultNumThreads;

    /// <summary>
    ///     Enable thinking/chain-of-thought mode for models that support it.
    ///     When true, sends think:true to Ollama and captures the reasoning separately.
    ///     Models like gemma4, qwen3, deepseek-r1 support this natively.
    ///     Default: false (faster inference, sufficient for most classification).
    /// </summary>
    public bool EnableThinking { get; set; }
}
