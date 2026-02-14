namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Callback interface for broadcasting LLM classification results.
///     Implemented by the UI layer to push results via SignalR.
/// </summary>
public interface ILlmResultCallback
{
    /// <summary>
    ///     Called when background LLM classification produces a result.
    /// </summary>
    Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default);
}
