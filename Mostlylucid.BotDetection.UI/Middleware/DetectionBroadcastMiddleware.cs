using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Middleware that broadcasts REAL detection results to the SignalR dashboard hub.
///     Must run AFTER BotDetectionMiddleware to access the detection results.
/// </summary>
public class DetectionBroadcastMiddleware
{
    private readonly ILogger<DetectionBroadcastMiddleware> _logger;
    private readonly RequestDelegate _next;

    public DetectionBroadcastMiddleware(
        RequestDelegate next,
        ILogger<DetectionBroadcastMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        DetectionDescriptionService descriptionService)
    {
        // Call next middleware first (so detection runs)
        await _next(context);

        // After response, broadcast detection result if available
        try
        {
            if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
                evidenceObj is AggregatedEvidence evidence)
            {
                // Get signature from context
                string? primarySignature = null;
                if (context.Items.TryGetValue("BotDetection.Signatures", out var sigObj) &&
                    sigObj is string sigJson)
                {
                    try
                    {
                        var sigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(sigJson);
                        primarySignature = sigs?.GetValueOrDefault("primary");
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to deserialize primary signature JSON");
                    }
                }

                var sigValue = primarySignature ?? GenerateFallbackSignature(context);

                // Build detection event
                var detection = new DashboardDetectionEvent
                {
                    RequestId = context.TraceIdentifier,
                    Timestamp = DateTime.UtcNow,
                    IsBot = evidence.BotProbability > 0.5,
                    BotProbability = evidence.BotProbability,
                    Confidence = evidence.Confidence,
                    RiskBand = evidence.RiskBand.ToString(),
                    BotType = evidence.PrimaryBotType?.ToString(),
                    BotName = evidence.PrimaryBotName,
                    Action = evidence.PolicyAction?.ToString() ?? evidence.TriggeredActionPolicyName ?? "Allow",
                    PolicyName = evidence.PolicyName ?? "Default",
                    Method = context.Request.Method,
                    Path = context.Request.Path.Value ?? "/",
                    StatusCode = context.Response.StatusCode,
                    ProcessingTimeMs = evidence.TotalProcessingTimeMs,
                    PrimarySignature = sigValue,
                    TopReasons = evidence.Contributions
                        .Where(c => !string.IsNullOrEmpty(c.Reason))
                        .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                        .Take(5)
                        .Select(c => c.Reason!)
                        .ToList()
                };

                // Store detection event in event store
                await eventStore.AddDetectionAsync(detection);

                // Upsert signature ledger (hit_count increments on conflict)
                // Extract individual signature factors if available
                string? ipSig = null, uaSig = null, clientSig = null;
                int factorCount = 1;
                if (context.Items.TryGetValue("BotDetection.Signatures", out var allSigsObj) &&
                    allSigsObj is string allSigsJson)
                {
                    try
                    {
                        var allSigs = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(allSigsJson);
                        if (allSigs != null)
                        {
                            ipSig = allSigs.GetValueOrDefault("ip");
                            uaSig = allSigs.GetValueOrDefault("ua");
                            clientSig = allSigs.GetValueOrDefault("clientSide");
                            factorCount = allSigs.Count(s => !string.IsNullOrEmpty(s.Value) && s.Key != "primary");
                        }
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        _logger.LogDebug(ex, "Failed to deserialize signature factors JSON");
                    }
                }

                var signature = new DashboardSignatureEvent
                {
                    SignatureId = Guid.NewGuid().ToString("N")[..12],
                    Timestamp = DateTime.UtcNow,
                    PrimarySignature = sigValue,
                    IpSignature = ipSig,
                    UaSignature = uaSig,
                    ClientSideSignature = clientSig,
                    FactorCount = Math.Max(1, factorCount),
                    RiskBand = evidence.RiskBand.ToString(),
                    HitCount = 1, // Will be incremented by DB on conflict
                    IsKnownBot = evidence.PrimaryBotType.HasValue,
                    BotName = evidence.PrimaryBotName
                };

                var updatedSignature = await eventStore.AddSignatureAsync(signature);

                // Broadcast detection and signature (with hit_count) to all connected clients
                await hubContext.Clients.All.BroadcastDetection(detection);
                await hubContext.Clients.All.BroadcastSignature(updatedSignature);

                // Fire-and-forget: generate plain-english description via Ollama for bot detections
                if (detection.IsBot)
                    _ = descriptionService.GenerateAndBroadcastAsync(detection);

                _logger.LogDebug(
                    "Broadcast detection: {Path} sig={Signature} prob={Probability:F2} hits={HitCount}",
                    detection.Path,
                    detection.PrimarySignature?[..Math.Min(8, detection.PrimarySignature.Length)],
                    detection.BotProbability,
                    updatedSignature.HitCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast detection");
        }
    }

    /// <summary>
    ///     Generate a fallback signature if the real one isn't available.
    /// </summary>
    private string GenerateFallbackSignature(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var ua = context.Request.Headers.UserAgent.ToString();
        var combined = $"{ip}:{ua}";

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
