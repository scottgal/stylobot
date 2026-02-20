namespace Mostlylucid.BotDetection.Llm.Ollama;

/// <summary>
///     Configuration for the Ollama HTTP LLM provider.
/// </summary>
public class OllamaProviderOptions
{
    /// <summary>Ollama API endpoint URL. Default: "http://localhost:11434"</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Ollama model to use. Default: "qwen3:0.6b"</summary>
    public string Model { get; set; } = "qwen3:0.6b";

    /// <summary>Number of CPU threads for Ollama inference. Default: 4</summary>
    public int NumThreads { get; set; } = 4;
}
