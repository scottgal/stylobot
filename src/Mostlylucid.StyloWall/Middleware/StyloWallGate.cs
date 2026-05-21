using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.StyloWall.Abstractions;
using Mostlylucid.StyloWall.Models;

namespace Mostlylucid.StyloWall.Middleware;

public sealed class StyloWallGate
{
    private readonly IOptionsMonitor<StyloWallOptions> _options;

    public StyloWallGate(IOptionsMonitor<StyloWallOptions> options)
    {
        _options = options;
    }

    public StyloWallVerdict Evaluate(HttpContext context)
    {
        var opts = _options.CurrentValue;
        if (!opts.Enabled) return StyloWallVerdict.PassThrough;

        var triggers = opts.Triggers;

        if (triggers.QueryString && TryQueryString(context, triggers.QueryStringKey, out var qsMode))
            return new StyloWallVerdict(qsMode, StyloWallTrigger.QueryString);

        if (triggers.AcceptHeader && TryAcceptHeader(context, out var acceptMode))
            return new StyloWallVerdict(acceptMode, StyloWallTrigger.AcceptHeader);

        if (opts.Routes.Count > 0 && TryRoute(context, opts.Routes, out var routeMode))
            return new StyloWallVerdict(routeMode, StyloWallTrigger.RouteRule);

        if (triggers.DetectionVerdict && TryVerdict(context, triggers.MinBotProbability, out var verdictMode))
            return new StyloWallVerdict(verdictMode, StyloWallTrigger.DetectionVerdict);

        return StyloWallVerdict.PassThrough;
    }

    private static bool TryQueryString(HttpContext context, string key, out string mode)
    {
        if (context.Request.Query.TryGetValue(key, out var value))
        {
            var raw = value.ToString();
            if (raw is "md" or "markdown")
            {
                mode = "markdown";
                return true;
            }
        }
        mode = string.Empty;
        return false;
    }

    private static bool TryAcceptHeader(HttpContext context, out string mode)
    {
        if (context.Request.Headers.TryGetValue("Accept", out var accept))
        {
            var raw = accept.ToString();
            if (raw.Contains("text/markdown", StringComparison.OrdinalIgnoreCase) ||
                raw.Contains("text/x-markdown", StringComparison.OrdinalIgnoreCase))
            {
                mode = "markdown";
                return true;
            }
        }
        mode = string.Empty;
        return false;
    }

    private static bool TryRoute(HttpContext context, List<StyloWallRouteRule> rules, out string mode)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        foreach (var rule in rules)
        {
            if (!path.StartsWith(rule.PathPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (rule.RequireBotVerdict && !context.IsBot()) continue;
            mode = string.IsNullOrEmpty(rule.Mode) ? "markdown" : rule.Mode;
            return true;
        }
        mode = string.Empty;
        return false;
    }

    private static bool TryVerdict(HttpContext context, double minProbability, out string mode)
    {
        var result = context.GetBotDetectionResult();
        if (result is null)
        {
            mode = string.Empty;
            return false;
        }

        if (result.BotType == BotType.AiBot && result.ConfidenceScore >= minProbability)
        {
            mode = "markdown";
            return true;
        }

        mode = string.Empty;
        return false;
    }
}
