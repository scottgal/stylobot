using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

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

        var result = httpContext.GetBotDetectionResult();

        if (result == null || !result.IsBot || result.ConfidenceScore < _minConfidence)
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
