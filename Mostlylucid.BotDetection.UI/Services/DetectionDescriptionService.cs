using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Generates plain-english descriptions for bot detections using ILlmProvider (if available).
///     Runs asynchronously after detection broadcast - does not block the request pipeline.
/// </summary>
public class DetectionDescriptionService
{
    private const string DescriptionPrompt = @"You are a cybersecurity analyst. Given a bot detection event, write ONE concise sentence (max 30 words) describing what this visitor likely is and why it was flagged. Be specific about the evidence. No JSON, no markdown - just the plain sentence.

Detection:
- Bot probability: {PROBABILITY}
- Confidence: {CONFIDENCE}
- Risk band: {RISK_BAND}
- Bot type: {BOT_TYPE}
- Bot name: {BOT_NAME}
- Action taken: {ACTION}
- Top reasons: {REASONS}
- Path: {PATH}
- Method: {METHOD}";

    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<DetectionDescriptionService> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly SemaphoreSlim _throttle = new(2);

    public DetectionDescriptionService(
        ILogger<DetectionDescriptionService> logger,
        IOptions<BotDetectionOptions> options,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _options = options.Value;
        _hubContext = hubContext;
        _serviceProvider = serviceProvider;
    }

    public async Task GenerateAndBroadcastAsync(DashboardDetectionEvent detection, CancellationToken ct = default)
    {
        if (!detection.IsBot || detection.BotProbability < 0.4)
            return;

        // Try to resolve ILlmProvider from DI (from Llm plugin packages)
        var providerType = Type.GetType("Mostlylucid.BotDetection.Llm.ILlmProvider, Mostlylucid.BotDetection.Llm");
        var provider = providerType != null ? _serviceProvider.GetService(providerType) : null;
        if (provider == null)
            return;

        if (!await _throttle.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            _logger.LogDebug("Description generation throttled for {RequestId}", detection.RequestId);
            return;
        }

        try
        {
            var description = await GenerateDescriptionAsync(provider, detection, ct);
            if (!string.IsNullOrWhiteSpace(description))
            {
                detection.Description = description;
                // Beacon-only: signal that signature data changed
                await _hubContext.Clients.All.BroadcastInvalidation("signature");
                _logger.LogDebug("Broadcast description invalidation for {RequestId}", detection.RequestId);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to generate description for {RequestId}", detection.RequestId);
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<string?> GenerateDescriptionAsync(object provider, DashboardDetectionEvent detection, CancellationToken ct)
    {
        var prompt = DescriptionPrompt
            .Replace("{PROBABILITY}", $"{detection.BotProbability:F2}")
            .Replace("{CONFIDENCE}", $"{detection.Confidence:F2}")
            .Replace("{RISK_BAND}", detection.RiskBand)
            .Replace("{BOT_TYPE}", detection.BotType ?? "Unknown")
            .Replace("{BOT_NAME}", detection.BotName ?? "Unknown")
            .Replace("{ACTION}", detection.Action ?? "Allow")
            .Replace("{REASONS}", detection.TopReasons.Count > 0
                ? string.Join("; ", detection.TopReasons.Take(3))
                : "No specific reasons")
            .Replace("{PATH}", detection.Path)
            .Replace("{METHOD}", detection.Method);

        try
        {
            // Use ILlmProvider.CompleteAsync via reflection
            var requestType = Type.GetType("Mostlylucid.BotDetection.Llm.LlmRequest, Mostlylucid.BotDetection.Llm");
            if (requestType == null) return null;

            var request = Activator.CreateInstance(requestType);
            if (request == null) return null;

            requestType.GetProperty("Prompt")!.SetValue(request, prompt);
            requestType.GetProperty("Temperature")!.SetValue(request, 0.3f);
            requestType.GetProperty("MaxTokens")!.SetValue(request, 100);
            requestType.GetProperty("TimeoutMs")!.SetValue(request, 10000);

            var completeMethod = provider.GetType().GetMethod("CompleteAsync");
            if (completeMethod == null) return null;

            var task = (Task<string>)completeMethod.Invoke(provider, new[] { request, ct })!;
            var response = (await task).Trim();

            if (response.StartsWith('"') && response.EndsWith('"'))
                response = response[1..^1];

            if (response.Length > 200)
                response = response[..197] + "...";

            return string.IsNullOrWhiteSpace(response) ? null : response;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LLM description generation failed");
            return null;
        }
    }
}
