namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Caller-owned LLM-call adapter. Translates an <see cref="EphemeralPrompt"/>
///     into a concrete provider call and returns the parsed result. Throws on
///     transport error, parse failure, or quota exhaustion — the coordinator
///     catches and counts the fault, no writeback fires, picker surfaces the
///     item again next tick.
/// </summary>
public interface IEphemeralLlmInvoker<TResult>
{
    Task<TResult> InvokeAsync(EphemeralPrompt prompt, CancellationToken ct);
}
