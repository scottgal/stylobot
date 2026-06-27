using Mostlylucid.Atoms.Ephemeral;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Persists a signature-naming result via the existing
///     <see cref="ILlmResultCallback.OnSignatureDescriptionAsync"/> path — the same
///     callback the legacy <c>LlmDescriptionCoordinator.ProcessSignatureAsync</c>
///     invoked. Always releases the in-flight reservation in <c>finally</c> so a
///     writeback exception still frees the key for the next tick. Invoker-side
///     failures are reclaimed by <see cref="SignatureInFlightSet"/>'s staleness
///     window (Option C from the EC6a spec).
/// </summary>
public sealed class SignatureLlmWriteback : IEphemeralWriteback<SignaturePickItem, SignatureNamingResult>
{
    private readonly ILlmResultCallback? _resultCallback;
    private readonly SignatureInFlightSet _inFlight;

    public SignatureLlmWriteback(SignatureInFlightSet inFlight, ILlmResultCallback? resultCallback = null)
    {
        _inFlight = inFlight;
        _resultCallback = resultCallback;
    }

    public async Task ApplyAsync(SignaturePickItem item, SignatureNamingResult result, CancellationToken ct)
    {
        try
        {
            if (_resultCallback is not null)
                await _resultCallback.OnSignatureDescriptionAsync(
                    item.Signature,
                    result.Name,
                    result.Description ?? result.Name,
                    ct);
        }
        finally
        {
            _inFlight.Release(item.Signature);
        }
    }
}
