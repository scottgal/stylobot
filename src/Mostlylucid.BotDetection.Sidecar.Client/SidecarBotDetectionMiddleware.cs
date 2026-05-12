using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Stylobot.Detection.V1;

namespace Mostlylucid.BotDetection.Sidecar.Client;

public sealed class SidecarBotDetectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DetectionService.DetectionServiceClient _client;
    private readonly int _timeoutMs;
    private readonly FailureMode _onFailure;
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
        _onFailure = options.Value.OnFailure;
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
            await _next(context);
            return;
        }
        catch (RpcException ex)
        {
            _logger.LogWarning(ex, "Sidecar gRPC call failed; applying OnFailure={Mode}", _onFailure);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sidecar detection error; applying OnFailure={Mode}", _onFailure);
        }

        // Failure path: same three-mode applier shape as BotDetectionMiddleware.
        context.Response.Headers["X-StyloBot-Failed"] = "sidecar";
        switch (_onFailure)
        {
            case FailureMode.FailClosed:
                context.Response.StatusCode = 503;
                return;
            case FailureMode.LogOnly:
            case FailureMode.FailOpen:
            default:
                await _next(context);
                return;
        }
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
