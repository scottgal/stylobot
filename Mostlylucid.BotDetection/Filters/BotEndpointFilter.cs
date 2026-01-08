using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Endpoint filter for minimal APIs that blocks bot requests.
/// </summary>
/// <example>
///     app.MapGet("/api/sensitive", () => "data")
///     .AddEndpointFilter(new BlockBotsEndpointFilter());
///     // Or use the extension method:
///     app.MapGet("/api/sensitive", () => "data")
///     .BlockBots();
/// </example>
public class BlockBotsEndpointFilter : IEndpointFilter
{
    private readonly bool _allowSearchEngines;
    private readonly bool _allowVerifiedBots;
    private readonly double _minConfidence;
    private readonly int _statusCode;

    public BlockBotsEndpointFilter(
        bool allowVerifiedBots = false,
        bool allowSearchEngines = false,
        double minConfidence = 0.0,
        int statusCode = 403)
    {
        _allowVerifiedBots = allowVerifiedBots;
        _allowSearchEngines = allowSearchEngines;
        _minConfidence = minConfidence;
        _statusCode = statusCode;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = context.HttpContext.GetBotDetectionResult();

        if (result == null || !result.IsBot || result.ConfidenceScore < _minConfidence) return await next(context);

        // Check if we should allow this type of bot
        if (_allowVerifiedBots && result.BotType == BotType.VerifiedBot) return await next(context);

        if (_allowSearchEngines && (result.BotType == BotType.SearchEngine || result.BotType == BotType.VerifiedBot))
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