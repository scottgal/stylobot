using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR
///     and writes names/descriptions back to the server-side caches
///     so HTMX partial re-renders pick up the new data.
/// </summary>
public class LlmResultSignalRCallback : ILlmResultCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly VisitorListCache _visitorCache;
    private readonly ILogger<LlmResultSignalRCallback> _logger;

    public LlmResultSignalRCallback(
        ILogger<LlmResultSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        SignatureAggregateCache signatureCache,
        VisitorListCache visitorCache)
    {
        _logger = logger;
        _hubContext = hubContext;
        _signatureCache = signatureCache;
        _visitorCache = visitorCache;
    }

    public async Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
    {
        // Beacon-only: signal that signature data changed, clients re-fetch via HTMX
        await _hubContext.Clients.All.BroadcastInvalidation("signature");
        _logger.LogDebug("Broadcast LLM description invalidation for {RequestId}", requestId);
    }

    public async Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        // Write back to caches FIRST so the subsequent HTMX re-render picks up the new name
        _signatureCache.ApplyBotName(signature, name, description);

        var visitor = _visitorCache.Get(signature);
        if (visitor != null)
        {
            lock (visitor.SyncRoot)
            {
                visitor.BotName = name;
                visitor.Narrative = description;
            }
        }

        // Beacon-only: invalidate signature widgets + the specific signature
        await _hubContext.Clients.All.BroadcastInvalidation("signature");
        await _hubContext.Clients.All.BroadcastInvalidation(signature);
        _logger.LogInformation("Applied LLM bot name for {Signature}: '{Name}'",
            signature[..Math.Min(8, signature.Length)], name);
    }

    public async Task OnScoreNarrativeAsync(string signature, string narrative, CancellationToken ct = default)
    {
        // Write narrative to visitor cache
        var visitor = _visitorCache.Get(signature);
        if (visitor != null)
        {
            lock (visitor.SyncRoot)
            {
                visitor.Narrative = narrative;
            }
        }

        // Beacon-only: invalidate the specific signature widget
        await _hubContext.Clients.All.BroadcastInvalidation(signature);
        _logger.LogDebug("Broadcast score narrative invalidation for {Signature}",
            signature[..Math.Min(8, signature.Length)]);
    }
}
