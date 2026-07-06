using System.Text.Json;
using Mostlylucid.Ephemeral.Atoms.Llm;
using Mostlylucid.BotDetection.Identity;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Adapts an <see cref="EphemeralPrompt"/> onto
///     <see cref="IBotNameSynthesizer.SynthesizeDetailedAsync"/> for the per-
///     fingerprint LLM-naming path. Resolves the latest signal snapshot for the
///     pick's fingerprint from <see cref="IFingerprintStore.GetDisplayNameHistoryAsync"/>
///     (the matcher persists a snapshot per <c>InducedName</c> write via the N3
///     history table) and hands it to the synthesizer. When no history snapshot is
///     available -- legacy rows pre-N3, or a fingerprint whose induced name was
///     written before <c>signal_snapshot_json</c> was populated -- the invoker
///     falls back to a minimal signal dict carrying just the induced name; the
///     synthesizer's prompt template handles the thin-prior case.
///     Throws when the synthesizer reports not-ready or yields a blank name so
///     the <see cref="EphemeralLlmCoordinator{TItem,TResult}"/> counts the fault
///     and skips writeback -- the picker surfaces the same fingerprint again
///     next tick.
/// </summary>
public sealed class FingerprintLlmInvoker : IEphemeralLlmInvoker<FingerprintNamingResult>
{
    private readonly IBotNameSynthesizer _synthesizer;
    private readonly IFingerprintStore _store;

    public FingerprintLlmInvoker(IBotNameSynthesizer synthesizer, IFingerprintStore store)
    {
        _synthesizer = synthesizer;
        _store = store;
    }

    public async Task<FingerprintNamingResult> InvokeAsync(EphemeralPrompt prompt, CancellationToken ct)
    {
        if (!_synthesizer.IsReady)
            throw new InvalidOperationException("IBotNameSynthesizer is not ready.");

        var payload = JsonSerializer.Deserialize<FingerprintNamingPrompter.FingerprintPromptPayload>(prompt.UserPrompt)
                      ?? throw new InvalidOperationException(
                          "FingerprintLlmInvoker: failed to deserialize prompt payload.");

        var signals = await ResolveSignalsAsync(payload, ct);

        var (name, description) = await _synthesizer.SynthesizeDetailedAsync(signals, ct: ct);

        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("IBotNameSynthesizer returned an empty name.");

        return new FingerprintNamingResult(name, description ?? name);
    }

    /// <summary>
    ///     Pulls the most recent name-change history row's <c>SignalSnapshotJson</c>
    ///     for the picked fingerprint. The matcher writes a snapshot on every
    ///     <c>UpdateInducedNameAsync</c>; the freshest entry is what triggered
    ///     this drift pick, so its signals are the right input for the LLM.
    ///     Falls back to a minimal dict on legacy rows / parse failures so the
    ///     synthesizer always has something to consume.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, object?>> ResolveSignalsAsync(
        FingerprintNamingPrompter.FingerprintPromptPayload payload,
        CancellationToken ct)
    {
        try
        {
            var history = await _store.GetDisplayNameHistoryAsync(payload.FingerprintId, limit: 1, ct);
            if (history.Count > 0
                && !string.IsNullOrEmpty(history[0].SignalSnapshotJson))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    history[0].SignalSnapshotJson!);
                if (parsed is { Count: > 0 })
                    return parsed;
            }
        }
        catch (JsonException)
        {
            // Malformed snapshot JSON -- fall through to the minimal dict.
        }

        var fallback = new Dictionary<string, object?>(2);
        if (!string.IsNullOrEmpty(payload.InducedName))
            fallback["fingerprint.induced_name"] = payload.InducedName;
        fallback["fingerprint.id"] = payload.FingerprintId;
        return fallback;
    }
}
