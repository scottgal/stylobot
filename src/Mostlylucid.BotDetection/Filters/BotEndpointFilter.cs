using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Endpoint filter for minimal APIs that blocks bot requests.
///     By default blocks ALL bots. Use allow* parameters to whitelist specific bot types.
///     Scrapers and malicious bots are blocked by default but CAN be allowed (e.g., for honeypots).
/// </summary>
/// <example>
///     app.MapGet("/api/sensitive", () => "data")
///     .BlockBots();
///
///     app.MapGet("/api/products", () => "data")
///     .BlockBots(allowSearchEngines: true, allowSocialMediaBots: true);
///
///     app.MapGet("/honeypot", () => "come in")
///     .BlockBots(allowScrapers: true, allowMaliciousBots: true);
///
///     app.MapGet("/api/geo-restricted", () => "data")
///     .BlockBots(blockCountries: "CN,RU", blockVpn: true);
/// </example>
public class BlockBotsEndpointFilter : IEndpointFilter
{
    private readonly bool _allowVerifiedBots;
    private readonly bool _allowSearchEngines;
    private readonly bool _allowSocialMediaBots;
    private readonly bool _allowMonitoringBots;
    private readonly bool _allowAiBots;
    private readonly bool _allowGoodBots;
    private readonly bool _allowScrapers;
    private readonly bool _allowMaliciousBots;
    private readonly bool _allowTools;
    private readonly double _minConfidence;
    private readonly int _statusCode;
    private readonly string? _blockCountries;
    private readonly string? _allowCountries;
    private readonly bool _blockVpn;
    private readonly bool _blockProxy;
    private readonly bool _blockDatacenter;
    private readonly bool _blockTor;

    public BlockBotsEndpointFilter(
        bool allowVerifiedBots = false,
        bool allowSearchEngines = false,
        bool allowSocialMediaBots = false,
        bool allowMonitoringBots = false,
        bool allowAiBots = false,
        bool allowGoodBots = false,
        bool allowScrapers = false,
        bool allowMaliciousBots = false,
        double minConfidence = 0.0,
        int statusCode = 403,
        string? blockCountries = null,
        string? allowCountries = null,
        bool blockVpn = false,
        bool blockProxy = false,
        bool blockDatacenter = false,
        bool blockTor = false,
        bool allowTools = false)
    {
        _allowVerifiedBots = allowVerifiedBots;
        _allowSearchEngines = allowSearchEngines;
        _allowSocialMediaBots = allowSocialMediaBots;
        _allowMonitoringBots = allowMonitoringBots;
        _allowAiBots = allowAiBots;
        _allowGoodBots = allowGoodBots;
        _allowScrapers = allowScrapers;
        _allowMaliciousBots = allowMaliciousBots;
        _allowTools = allowTools;
        _minConfidence = minConfidence;
        _statusCode = statusCode;
        _blockCountries = blockCountries;
        _allowCountries = allowCountries;
        _blockVpn = blockVpn;
        _blockProxy = blockProxy;
        _blockDatacenter = blockDatacenter;
        _blockTor = blockTor;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;

        // Check network/geo blocking first (applies to all traffic, not just bots)
        if (BotTypeFilter.IsBlockedByNetwork(httpContext,
                _blockCountries, _allowCountries, _blockVpn, _blockProxy, _blockDatacenter, _blockTor))
        {
            return Results.Json(new
            {
                error = "Access denied",
                blocked = true,
                reason = "network"
            }, statusCode: _statusCode);
        }

        // Honour an upstream API-key bypass: if the middleware skipped
        // detection because a trusted key disabled all detectors, we pass
        // through too. Re-running detection here would defeat the bypass.
        var apiKeyBypass = httpContext.Items.TryGetValue("BotDetection.ApiKeyBypass", out var bp)
            && bp is true;
        if (apiKeyBypass)
            return await next(context);

        var result = httpContext.GetBotDetectionResult();

        // The middleware may have used the "static" policy for paths matching
        // static-asset extensions (.xml for sitemaps, .json for manifests, .txt
        // for robots.txt). That policy is intentionally lax: only
        // FastPathReputation runs, no UA / header / behavioural checks.
        // .BlockBots() represents EXPLICIT developer intent that bots should
        // be refused on this route, which overrides path-based laxness. If the
        // static policy was used (or detection didn't run at all), re-run
        // with the default policy so the UA-based classification is honoured.
        var usedPolicy = httpContext.Items.TryGetValue("BotDetection.PolicyName", out var pn)
            ? pn?.ToString()
            : null;

        if (result is null ||
            string.Equals(usedPolicy, "static", StringComparison.OrdinalIgnoreCase))
        {
            result = await EnsureDetectionAsync(httpContext);
            if (result is null)
                return Results.Json(new
                {
                    error = "Access denied",
                    blocked = true,
                    reason = "detection-unavailable"
                }, statusCode: _statusCode);
        }

        if (!result.IsBot || result.ConfidenceScore < _minConfidence)
            return await next(context);

        // Check if this bot type is allowed through (shared logic)
        if (BotTypeFilter.IsBotTypeAllowed(result.BotType,
                _allowVerifiedBots, _allowSearchEngines, _allowSocialMediaBots,
                _allowMonitoringBots, _allowAiBots, _allowGoodBots,
                _allowScrapers, _allowMaliciousBots, _allowTools))
            return await next(context);

        return Results.Json(new
        {
            error = "Access denied",
            isBot = true,
            botType = result.BotType?.ToString(),
            confidence = result.ConfidenceScore
        }, statusCode: _statusCode);
    }

    /// <summary>
    ///     Runs the detection pipeline with the default policy when the
    ///     middleware path-policy short-circuited the request (sitemap.xml on
    ///     a static-asset policy, etc.). Writes the result back to
    ///     HttpContext.Items so subsequent reads (TagHelpers, other filters)
    ///     see the same verdict.
    /// </summary>
    private static async Task<BotDetectionResult?> EnsureDetectionAsync(HttpContext httpContext)
    {
        var orchestrator = httpContext.RequestServices.GetService<IDetectionOrchestrator>();
        var policies = httpContext.RequestServices.GetService<IPolicyRegistry>();
        if (orchestrator is null || policies is null) return null;

        try
        {
            var evidence = await orchestrator.DetectWithPolicyAsync(
                httpContext, policies.DefaultPolicy, httpContext.RequestAborted);

            var detectionResult = new BotDetectionResult
            {
                IsBot = evidence.BotProbability >= 0.5,
                BotType = evidence.PrimaryBotType,
                BotName = evidence.PrimaryBotName,
                ConfidenceScore = evidence.BotProbability,
                ProcessingTimeMs = evidence.TotalProcessingTimeMs,
            };

            httpContext.Items["BotDetectionResult"] = detectionResult;
            httpContext.Items["BotDetection.AggregatedEvidence"] = evidence;

            // Count the on-demand detection in the global stats counter so
            // /bot-detection/stats reflects requests that hit endpoint
            // filters but bypassed the middleware's normal detection path.
            httpContext.RequestServices
                .GetService<IBotDetectionService>()
                ?.RecordDetection(detectionResult);

            return detectionResult;
        }
        catch (Exception ex)
        {
            httpContext.RequestServices
                .GetService<ILoggerFactory>()
                ?.CreateLogger("BlockBotsEndpointFilter")
                .LogWarning(ex,
                    "On-demand detection failed for {Path}; returning fail-closed",
                    httpContext.Request.Path);
            return null;
        }
    }
}

/// <summary>
///     Endpoint filter that requires human visitors (blocks all bots).
/// </summary>
public class RequireHumanEndpointFilter : IEndpointFilter
{
    private readonly int _statusCode;

    public RequireHumanEndpointFilter(int statusCode = 403)
    {
        _statusCode = statusCode;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = context.HttpContext.GetBotDetectionResult();

        if (result == null || !result.IsBot) return await next(context);

        return Results.Json(new
        {
            error = "This endpoint requires human verification",
            isBot = true,
            botType = result.BotType?.ToString()
        }, statusCode: _statusCode);
    }
}
