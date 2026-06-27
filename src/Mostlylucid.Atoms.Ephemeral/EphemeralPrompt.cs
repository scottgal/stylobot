namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Provider-agnostic prompt payload handed from <see cref="IEphemeralPrompter{TItem}"/>
///     to <see cref="IEphemeralLlmInvoker{TResult}"/>. The invoker adapts this onto
///     whatever concrete LLM SDK / HTTP shape it talks (OpenAI / Anthropic / Ollama /
///     Azure / etc.). The coordinator never inspects the payload.
/// </summary>
public sealed record EphemeralPrompt(
    string SystemPrompt,
    string UserPrompt,
    int MaxTokens,
    double Temperature);