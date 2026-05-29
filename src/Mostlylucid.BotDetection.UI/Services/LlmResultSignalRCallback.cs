using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR
///     and writes names/descriptions back to the server-side caches
///     so HTMX partial re-renders pick up the new data.
///     Also propagates the LLM-derived name to <see cref="IFingerprintStore"/>
///     so the matcher's persisted display name (and therefore the CLI / response
///     header surface) picks up the better name on subsequent requests.
/// </summary>
public class LlmResultSignalRCallback : ILlmResultCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly VisitorListCache _visitorCache;
    private readonly IDashboardEventStore _eventStore;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly StyloBotDashboardOptions _dashboardOptions;
    private readonly ILogger<LlmResultSignalRCallback> _logger;

    public LlmResultSignalRCallback(
        ILogger<LlmResultSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        SignatureAggregateCache signatureCache,
        VisitorListCache visitorCache,
        IDashboardEventStore eventStore,
        IFingerprintStore? fingerprintStore = null,
        IOptions<StyloBotDashboardOptions>? dashboardOptions = null)
    {
        _logger = logger;
        _hubContext = hubContext;
        _signatureCache = signatureCache;
        _visitorCache = visitorCache;
        _eventStore = eventStore;
        _fingerprintStore = fingerprintStore;
        _dashboardOptions = dashboardOptions?.Value ?? new StyloBotDashboardOptions();
    }

    public Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
    {
        // Beacon-only: signal that signature data changed, clients re-fetch via HTMX.
        // Through the constrainer so the dashboard's outbound 10s window applies.
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Broadcast LLM description invalidation for {RequestId}", requestId);
        return Task.CompletedTask;
    }

    public async Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        // Persist to BOTH SQLite stores first. The in-memory caches are derived state; on
        // restart, only what's in SQLite survives.
        await _eventStore.UpdateSignatureBotNameAsync(signature, name, description, ct);
        if (_fingerprintStore is not null)
            await _fingerprintStore.UpdateDisplayNameForSignatureAsync(signature, name, DateTime.UtcNow, ct);

        // Then update in-memory caches so the next HTMX re-render picks up the new name
        // without needing to read from SQLite.
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

        // Beacon-only: invalidate signature widgets + the specific signature.
        // Through the constrainer so the dashboard's outbound 10s window applies.
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        SignalRBroadcastConstrainer.Queue(_hubContext, signature,   _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogInformation("Applied LLM bot name for {Signature}: '{Name}'",
            signature[..Math.Min(8, signature.Length)], name);
    }

    public Task OnScoreNarrativeAsync(string signature, string narrative, CancellationToken ct = default)
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

        // Beacon-only: invalidate the specific signature widget.
        // Through the constrainer so the dashboard's outbound 10s window applies.
        SignalRBroadcastConstrainer.Queue(_hubContext, signature, _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Broadcast score narrative invalidation for {Signature}",
            signature[..Math.Min(8, signature.Length)]);
        return Task.CompletedTask;
    }
}
