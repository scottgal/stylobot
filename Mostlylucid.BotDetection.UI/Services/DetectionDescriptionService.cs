using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;
using OllamaSharp;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Generates plain-english descriptions for bot detections using a local Ollama LLM.
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
    private readonly SemaphoreSlim _throttle = new(2); // Max 2 concurrent LLM calls for descriptions

    public DetectionDescriptionService(
        ILogger<DetectionDescriptionService> logger,
        IOptions<BotDetectionOptions> options,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext)
    {
        _logger = logger;
        _options = options.Value;
        _hubContext = hubContext;
    }

    /// <summary>
    ///     Generate a plain-english description for a detection event and broadcast it via SignalR.
    ///     This is fire-and-forget - call without awaiting from the middleware.
    /// </summary>
    public async Task GenerateAndBroadcastAsync(DashboardDetectionEvent detection, CancellationToken ct = default)
    {
        // Only generate for bot detections with reasonable probability
        if (!detection.IsBot || detection.BotProbability < 0.4)
            return;

        // Check if Ollama endpoint is configured (descriptions work even when LLM detection is disabled)
        if (string.IsNullOrEmpty(_options.AiDetection.Ollama.Endpoint))
            return;

        if (!await _throttle.WaitAsync(TimeSpan.FromSeconds(2), ct))
        {
            _logger.LogDebug("Description generation throttled for {RequestId}", detection.RequestId);
            return;
        }

        try
        {
            var description = await GenerateDescriptionAsync(detection, ct);
            if (!string.IsNullOrWhiteSpace(description))
            {
                detection.Description = description;
                await _hubContext.Clients.All.BroadcastDescriptionUpdate(detection.RequestId, description);
                _logger.LogDebug("Broadcast description for {RequestId}: {Description}",
                    detection.RequestId, description.Length > 80 ? description[..80] + "..." : description);
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

    private async Task<string?> GenerateDescriptionAsync(DashboardDetectionEvent detection, CancellationToken ct)
    {
        var endpoint = _options.AiDetection.Ollama.Endpoint;
        var model = _options.AiDetection.Ollama.Model;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10)); // 10s timeout for descriptions

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

        var ollama = new OllamaApiClient(endpoint)
        {
            SelectedModel = model
        };

        var chat = new Chat(ollama);
        var responseBuilder = new StringBuilder();

        await foreach (var token in chat.SendAsync(prompt, cts.Token))
            responseBuilder.Append(token);

        var response = responseBuilder.ToString().Trim();

        // Clean up: remove quotes, markdown, etc.
        if (response.StartsWith('"') && response.EndsWith('"'))
            response = response[1..^1];

        // Truncate if too long
        if (response.Length > 200)
            response = response[..197] + "...";

        return string.IsNullOrWhiteSpace(response) ? null : response;
    }
}
