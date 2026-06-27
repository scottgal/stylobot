using System.Text.Json;
using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Builds the <see cref="EphemeralPrompt"/> for the per-fingerprint LLM-naming
///     path. The synthesizer (<see cref="IBotNameSynthesizer.SynthesizeDetailedAsync"/>)
///     owns its prompt template internally and consumes a signals dict, not a free-
///     text prompt -- the legacy per-signature prompter (deleted in LL1) dealt
///     with this same shape by JSON-serialising the signals into
///     <see cref="EphemeralPrompt.UserPrompt"/> and letting the invoker deserialise
///     them back. Same pattern here: the prompter is sync (it cannot await a DB
///     read) so it serialises the lightweight pick item (fingerprint id + induced
///     name) and the async <see cref="FingerprintLlmInvoker"/> resolves the latest
///     signal snapshot from <c>IFingerprintStore.GetDisplayNameHistoryAsync</c>
///     before calling the synthesizer. MaxTokens / Temperature are the
///     plan-specified defaults (256 / 0.2); the synthesizer ignores them but the
///     <see cref="EphemeralPrompt"/> contract requires values.
/// </summary>
public sealed class FingerprintNamingPrompter : IEphemeralPrompter<FingerprintPickItem>
{
    private const string FingerprintPromptSystem = "stylobot.fingerprint-naming";
    private const int DefaultMaxTokens = 256;
    private const double DefaultTemperature = 0.2;

    public EphemeralPrompt Build(FingerprintPickItem item)
    {
        var json = JsonSerializer.Serialize(new FingerprintPromptPayload(
            FingerprintId: item.FingerprintId,
            InducedName: item.InducedName));
        return new EphemeralPrompt(
            SystemPrompt: FingerprintPromptSystem,
            UserPrompt: json,
            MaxTokens: DefaultMaxTokens,
            Temperature: DefaultTemperature);
    }

    /// <summary>
    ///     Serialised payload handed from the (sync) prompter to the (async)
    ///     invoker through <see cref="EphemeralPrompt.UserPrompt"/>.
    /// </summary>
    internal sealed record FingerprintPromptPayload(
        string FingerprintId,
        string? InducedName);
}
