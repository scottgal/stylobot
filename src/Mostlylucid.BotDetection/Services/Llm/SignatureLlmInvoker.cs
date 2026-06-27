using System.Text.Json;
using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Adapts an <see cref="EphemeralPrompt"/> onto
///     <see cref="IBotNameSynthesizer.SynthesizeDetailedAsync"/>. Mirrors the
///     legacy <c>LlmDescriptionCoordinator.ProcessSignatureAsync</c> call shape
///     verbatim: gate on <c>IsReady</c>, hand the signals dict to the synthesizer,
///     surface the (name, description) tuple. Throws when the synthesizer reports
///     not-ready or yields a blank name so the EphemeralLlmCoordinator counts the
///     fault and skips writeback — the picker surfaces the item again next tick.
/// </summary>
public sealed class SignatureLlmInvoker : IEphemeralLlmInvoker<SignatureNamingResult>
{
    private readonly IBotNameSynthesizer _synthesizer;

    public SignatureLlmInvoker(IBotNameSynthesizer synthesizer)
        => _synthesizer = synthesizer;

    public async Task<SignatureNamingResult> InvokeAsync(EphemeralPrompt prompt, CancellationToken ct)
    {
        if (!_synthesizer.IsReady)
            throw new InvalidOperationException("IBotNameSynthesizer is not ready.");

        var signals = JsonSerializer.Deserialize<Dictionary<string, object?>>(prompt.UserPrompt)
                      ?? throw new InvalidOperationException("SignatureLlmInvoker: failed to deserialize signals payload.");

        var (name, description) = await _synthesizer.SynthesizeDetailedAsync(signals, ct: ct);

        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("IBotNameSynthesizer returned an empty name.");

        return new SignatureNamingResult(name, description ?? name);
    }
}
