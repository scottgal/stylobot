namespace Mostlylucid.BotDetection.Llm.LlamaSharp;

/// <summary>
///     Configuration for the LlamaSharp in-process LLM provider.
/// </summary>
public class LlamaSharpProviderOptions
{
    /// <summary>
    ///     Path to the GGUF model file or Hugging Face model identifier.
    ///     Examples:
    ///     - "./models/qwen-0.5b-q4_k_m.gguf" (local file)
    ///     - "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf" (HF auto-download)
    /// </summary>
    public string ModelPath { get; set; } = "Qwen/Qwen2.5-0.5B-Instruct-GGUF/qwen2.5-0.5b-instruct-q4_k_m.gguf";

    /// <summary>
    ///     Cache directory for downloaded models.
    ///     Default: ~/.cache/stylobot-models (or STYLOBOT_MODEL_CACHE env var)
    /// </summary>
    public string? ModelCacheDir { get; set; }

    /// <summary>Maximum context size for inference (tokens). Default: 512</summary>
    public int ContextSize { get; set; } = 512;

    /// <summary>Number of CPU threads for inference. Default: all cores</summary>
    public int ThreadCount { get; set; } = Environment.ProcessorCount;

    /// <summary>Temperature for generation. Default: 0.1f</summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>Maximum tokens to generate. Default: 150</summary>
    public int MaxTokens { get; set; } = 150;

    /// <summary>Timeout for inference in milliseconds. Default: 10000</summary>
    public int TimeoutMs { get; set; } = 10000;
}
