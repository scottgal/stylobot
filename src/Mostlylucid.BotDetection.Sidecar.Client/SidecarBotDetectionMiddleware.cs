using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Stylobot.Detection.V1;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarBotDetectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DetectionService.DetectionServiceClient _client;
    private readonly int _timeoutMs;
    private readonly ILogger<SidecarBotDetectionMiddleware> _logger;

    public SidecarBotDetectionMiddleware(
        RequestDelegate next,
        GrpcChannel channel,
        IOptions<SidecarClientOptions> options,
        ILogger<SidecarBotDetectionMiddleware> logger)
    {
        _next = next;
        _client = new DetectionService.DetectionServiceClient(channel);
        _timeoutMs = options.Value.TimeoutMs;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            var req = BuildRequest(context);
            var deadline = DateTime.UtcNow.AddMilliseconds(_timeoutMs);
            var response = await _client.DetectAsync(req, deadline: deadline);
            WriteToContext(context, response);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Sidecar gRPC call failed; failing open");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sidecar detection error; failing open");
        }

        await _next(context);
    }

    private static DetectRequest BuildRequest(HttpContext context)
    {
        var req = new DetectRequest
        {
            Method = context.Request.Method,
            Path = context.Request.Path.ToString(),
            RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            Protocol = context.Request.IsHttps ? "https" : "http",
        };

        foreach (var (key, values) in context.Request.Headers)
        {
            var value = values.ToString();
            if (!string.IsNullOrEmpty(value))
                req.Headers[key] = value;
        }

        return req;
    }

    private static void WriteToContext(HttpContext context, DetectResponse r)
    {
        var riskBand = (Mostlylucid.BotDetection.Orchestration.RiskBand)(int)r.RiskBand;
        var threatBand = (Mostlylucid.BotDetection.Orchestration.ThreatBand)(int)r.ThreatBand;

        var evidence = new AggregatedEvidence
        {
            BotProbability = r.BotProbability,
            Confidence = r.Confidence,
            RiskBand = riskBand,
            ThreatScore = r.ThreatScore,
            ThreatBand = threatBand,
            TotalProcessingTimeMs = r.ProcessingTimeMs,
        };

        var result = new BotDetectionResult
        {
            IsBot = r.IsBot,
            ConfidenceScore = r.Confidence,
        };

        if (!string.IsNullOrEmpty(r.BotType) && Enum.TryParse<BotType>(r.BotType, out var botType))
            result.BotType = botType;

        if (!string.IsNullOrEmpty(r.BotName))
            result.BotName = r.BotName;

        context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
        context.Items[BotDetectionMiddleware.BotDetectionResultKey] = result;
        context.Items[BotDetectionMiddleware.IsBotKey] = r.IsBot;
        context.Items[BotDetectionMiddleware.BotProbabilityKey] = (double)r.BotProbability;
        context.Items[BotDetectionMiddleware.BotConfidenceKey] = (double)r.Confidence;
    }
}
