namespace Mostlylucid.BotDetection.Llm;

/// <summary>
///     Abstraction over LLM inference backends (Ollama, LlamaSharp, etc.).
///     Registered as a singleton - only one provider active per application.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    ///     Send a prompt to the LLM and return the raw text completion.
    /// </summary>
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    ///     Send a prompt and return both content and thinking (if enabled).
    ///     Default implementation wraps CompleteAsync for backward compatibility.
    /// </summary>
    async Task<LlmResponse> CompleteWithThinkingAsync(LlmRequest request, CancellationToken ct = default)
    {
        var content = await CompleteAsync(request, ct);
        return new LlmResponse { Content = content };
    }

    /// <summary>
    ///     Whether the provider is initialized and ready to accept requests.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    ///     Perform any async initialization (model download, warm-up, etc.).
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);
}