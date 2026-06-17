using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM classification results via SignalR and routes the
///     new name through the one cache (<see cref="SignatureAggregateCache"/>) that
///     owns per-signature state. The cache writes through to durability
///     (<see cref="IDashboardEventStore"/> + <see cref="IFingerprintStore"/>) so the
///     callback never dual-writes -- the previous "callback writes DB + FP store +
///     SignatureAggregateCache + VisitorListCache" shape was the source of "two
///     names at the same instant" regressions.
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

    public async Task OnSignatureDescriptionAsync(string signature, string name, string description, CancellationToken ct = default)
    {
        await _signatureCache.ApplyBotNameAsync(signature, name, description, ct).ConfigureAwait(false);

        SignalRBroadcastConstrainer.Queue(_hubContext, "signature", _dashboardOptions.BroadcastMinIntervalMs);
        SignalRBroadcastConstrainer.Queue(_hubContext, signature,   _dashboardOptions.BroadcastMinIntervalMs);
        _logger.LogInformation("Applied LLM bot name for {Signature}: '{Name}'",
            signature[..Math.Min(8, signature.Length)], name);
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
