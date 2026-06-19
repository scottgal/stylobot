using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR. The LLM is
///     NOT a name writer: <c>FingerprintNameComposer</c> on the matcher is the
///     sole source of <c>Fingerprint.DisplayName</c>. The LLM contributes only
///     the human-readable DESCRIPTION (per-signature transient narrative) and
///     the score narrative. Removing the name write closes the parasite that
///     let the LLM's contextual inference (e.g. inferring "stylobot" because
///     the host header was stylobot.net) clobber the composer's authoritative
///     name on hot human signatures.
/// </summary>
public class LlmResultSignalRCallback : ILlmResultCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly StyloBotDashboardOptions _dashboardOptions;
    private readonly ILogger<LlmResultSignalRCallback> _logger;

    public LlmResultSignalRCallback(
        ILogger<LlmResultSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        SignatureAggregateCache signatureCache,
        IOptions<StyloBotDashboardOptions>? dashboardOptions = null)
    {
        _logger = logger;
        _hubContext = hubContext;
        _signatureCache = signatureCache;
        _dashboardOptions = dashboardOptions?.Value ?? new StyloBotDashboardOptions();
    }

    public Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
    {
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Broadcast LLM description invalidation for {RequestId}", requestId);
        return Task.CompletedTask;
    }

    public Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        // DESCRIPTION-ONLY. The matcher (FingerprintNameComposer) is the SOLE
        // writer of the name; the LLM's name suggestion is intentionally
        // ignored. Description is purely additive narrative, no contention.
        _signatureCache.ApplyDescription(signature, description);
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        SignalRBroadcastConstrainer.Queue(_hubContext, signature,   _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Applied LLM description for {Signature} (name ignored: matcher is sole writer)",
            signature[..Math.Min(8, signature.Length)]);
        return Task.CompletedTask;
    }

    public Task OnScoreNarrativeAsync(string signature, string narrative, CancellationToken ct = default)
    {
        _signatureCache.ApplyNarrative(signature, narrative);

        SignalRBroadcastConstrainer.Queue(_hubContext, signature, _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Broadcast score narrative invalidation for {Signature}",
            signature[..Math.Min(8, signature.Length)]);
        return Task.CompletedTask;
    }
}
