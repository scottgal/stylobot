using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Api.Middleware;

public class ResponseHeaderInjectionMiddleware
{
    private readonly RequestDelegate _next;

    public ResponseHeaderInjectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            ResponseHeaderInjection.InjectHeaders(context);
            return Task.CompletedTask;
        });

        await _next(context);
    }
}

public static class ResponseHeaderInjection
{
    private const double BotThreshold = 0.7;

    public static void InjectHeaders(HttpContext context)
    {
        if (!context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            || evidenceObj is not AggregatedEvidence evidence)
            return;

        var isBot = evidence.BotProbability >= BotThreshold;
        var action = evidence.RiskBand switch
        {
            RiskBand.High or RiskBand.VeryHigh => "Block",
            RiskBand.Medium => "Challenge",
            RiskBand.Elevated => "Throttle",
            _ => "Allow"
        };

        var headers = context.Response.Headers;
        headers["X-StyloBot-IsBot"] = isBot.ToString().ToLowerInvariant();
        headers["X-StyloBot-Probability"] = evidence.BotProbability.ToString("F2");
        headers["X-StyloBot-Confidence"] = evidence.Confidence.ToString("F2");
        headers["X-StyloBot-BotType"] = evidence.PrimaryBotType?.ToString() ?? "";
        headers["X-StyloBot-BotName"] = evidence.PrimaryBotName ?? "";
        headers["X-StyloBot-RiskBand"] = evidence.RiskBand.ToString();
        headers["X-StyloBot-Action"] = action;
        headers["X-StyloBot-ThreatScore"] = evidence.ThreatScore.ToString("F2");
        headers["X-StyloBot-ThreatBand"] = evidence.ThreatBand.ToString();
        headers["X-StyloBot-Policy"] = evidence.PolicyName ?? "";
        headers["X-StyloBot-RequestId"] = context.TraceIdentifier;
    }
}
