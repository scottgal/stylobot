using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR.
/// </summary>
public class LlmResultSignalRCallback : ILlmResultCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<LlmResultSignalRCallback> _logger;

    public LlmResultSignalRCallback(
        ILogger<LlmResultSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.BroadcastDescriptionUpdate(requestId, description);
        _logger.LogDebug("Broadcast LLM description for {RequestId}: {Description}",
            requestId, description.Length > 80 ? description[..80] + "..." : description);
    }

    public async Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        await _hubContext.Clients.All.BroadcastSignatureDescriptionUpdate(signature, name, description);
        _logger.LogDebug("Broadcast signature description for {Signature}: '{Name}'",
            signature[..Math.Min(8, signature.Length)], name);
    }
}
