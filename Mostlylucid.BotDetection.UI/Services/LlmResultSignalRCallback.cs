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
        await _hubContext.Clients.All.BroadcastDescriptionUpdate(requestId, description);
        _logger.LogDebug("Broadcast LLM description for {RequestId}: {Description}",
            requestId, description.Length > 80 ? description[..80] + "..." : description);
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

        // Then broadcast invalidation so clients re-fetch the partial
        await _hubContext.Clients.All.BroadcastSignatureDescriptionUpdate(signature, name, description);
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

        await _hubContext.Clients.All.BroadcastScoreNarrative(signature, narrative);
        _logger.LogDebug("Broadcast score narrative for {Signature}",
            signature[..Math.Min(8, signature.Length)]);
    }
}
