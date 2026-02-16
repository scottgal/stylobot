namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Callback interface for broadcasting LLM classification results.
///     Implemented by the UI layer to push results via SignalR.
/// </summary>
public interface ILlmResultCallback
{
    /// <summary>
    ///     Called when background LLM classification produces a result for a specific request.
    /// </summary>
    Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default);

    /// <summary>
    ///     Called when a signature description is generated (after reaching request threshold).
    ///     Broadcasts the name/description to all dashboard clients for that signature.
    /// </summary>
    Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default);
}
