using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR. Name writes go
///     directly to <see cref="IFingerprintStore"/> (the ONE LFU dict that owns
///     Fingerprint.DisplayName) with source "llm" so the timeline view records the
///     rename. The signature-scoped DESCRIPTION still lands on the aggregate cache
///     since description is per-signature transient narrative, not durable identity.
/// </summary>
public class LlmResultSignalRCallback : ILlmResultCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly IFingerprintStore _fingerprintStore;
    private readonly StyloBotDashboardOptions _dashboardOptions;
    private readonly ILogger<LlmResultSignalRCallback> _logger;

    public LlmResultSignalRCallback(
        ILogger<LlmResultSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        SignatureAggregateCache signatureCache,
        IFingerprintStore fingerprintStore,
        IOptions<StyloBotDashboardOptions>? dashboardOptions = null)
    {
        _logger = logger;
        _hubContext = hubContext;
        _signatureCache = signatureCache;
        _fingerprintStore = fingerprintStore;
        _dashboardOptions = dashboardOptions?.Value ?? new StyloBotDashboardOptions();
    }

    public Task OnLlmResultAsync(string requestId, string primarySignature, string description, CancellationToken ct = default)
    {
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogDebug("Broadcast LLM description invalidation for {RequestId}", requestId);
        return Task.CompletedTask;
    }

    public async Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        // The LLM is the LAST FALLBACK on the name pipeline. The matcher's
        // FingerprintNameComposer writes the authoritative DisplayName
        // (catalog-canonical for known bots, "Chrome 149 / macOS" composed
        // form for humans). When it's already populated, the LLM must NOT
        // overwrite -- otherwise a hot human signature with "Chrome 149 / macOS"
        // gets clobbered with whatever the LLM made up from contextual
        // signals ("stylobot" because the host was stylobot.net), which is
        // the staging bug that motivated this gate. The DESCRIPTION still
        // applies regardless -- the LLM's narrative caption is purely
        // additive, only the NAME-write contended with the composer.
        var existing = await _fingerprintStore
            .GetDisplayNamesBySignaturesAsync(new[] { signature }, ct)
            .ConfigureAwait(false);
        var existingName = existing.TryGetValue(signature, out var n) ? n : null;
        if (string.IsNullOrWhiteSpace(existingName))
        {
            await _fingerprintStore.UpdateDisplayNameForSignatureAsync(
                signature, name, DateTime.UtcNow, ct, source: "llm").ConfigureAwait(false);
            _logger.LogInformation("Applied LLM name for {Signature}: '{Name}' (composer had no name)",
                signature[..Math.Min(8, signature.Length)], name);
        }
        else
        {
            _logger.LogDebug("Skipped LLM name for {Signature}: composer name '{Existing}' kept; LLM tried '{Name}'",
                signature[..Math.Min(8, signature.Length)], existingName, name);
        }

        _signatureCache.ApplyDescription(signature, description);
        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        SignalRBroadcastConstrainer.Queue(_hubContext, signature,   _dashboardOptions.BroadcastMinIntervalMs);
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
