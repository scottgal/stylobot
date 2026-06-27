using System.Text.Json;
using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Builds the <see cref="EphemeralPrompt"/> for the signature naming path.
///     The legacy <c>LlmDescriptionCoordinator.ProcessSignatureAsync</c> handed
///     the signals dictionary straight to <c>IBotNameSynthesizer.SynthesizeDetailedAsync</c>
///     which owns the prompt template internally — there is no caller-side prompt
///     string in the legacy flow. To preserve that behaviour verbatim we serialize
///     the signals into <see cref="EphemeralPrompt.UserPrompt"/> as JSON and let
///     <see cref="SignatureLlmInvoker"/> deserialize them back before calling the
///     synthesizer. MaxTokens/Temperature surface as the plan-specified defaults
///     (256 / 0.2) — the synthesizer ignores them but the EphemeralPrompt contract
///     requires values.
/// </summary>
public sealed class SignatureNamingPrompter : IEphemeralPrompter<SignaturePickItem>
{
    private const string SignaturePromptSystem = "stylobot.signature-naming";
    private const int DefaultMaxTokens = 256;
    private const double DefaultTemperature = 0.2;

    public EphemeralPrompt Build(SignaturePickItem item)
    {
        var json = JsonSerializer.Serialize(item.Signals);
        return new EphemeralPrompt(
            SystemPrompt: SignaturePromptSystem,
            UserPrompt: json,
            MaxTokens: DefaultMaxTokens,
            Temperature: DefaultTemperature);
    }
}
