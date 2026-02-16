using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Endpoint filter that applies a named action policy to detected bots.
///     Use via .BotPolicy("policyName", actionPolicy: "throttle") on minimal API endpoints.
///     Delegates to the IActionPolicyRegistry for proper policy execution (throttle, challenge, redirect, etc.).
/// </summary>
public class BotPolicyEndpointFilter : IEndpointFilter
{
    private readonly string _policyName;
    private readonly string? _actionPolicy;
    private readonly double _blockThreshold;

    public BotPolicyEndpointFilter(string policyName = "default", string? actionPolicy = null,
        double blockThreshold = 0.0)
    {
        _policyName = policyName;
        _actionPolicy = actionPolicy;
        _blockThreshold = blockThreshold;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var result = httpContext.GetBotDetectionResult();

        if (result == null || !result.IsBot)
            return await next(context);

        if (_blockThreshold > 0.0 && result.ConfidenceScore < _blockThreshold)
            return await next(context);

        // If we have a named action policy, resolve and execute it via the registry
        if (_actionPolicy != null)
        {
            var registry = httpContext.RequestServices.GetService<IActionPolicyRegistry>();
            var policy = registry?.GetPolicy(_actionPolicy);

            if (policy != null)
            {
                // Store policy metadata for downstream middleware/logging
                httpContext.Items["BotDetection_ActionPolicy"] = _actionPolicy;
                httpContext.Items["BotDetection_PolicyName"] = _policyName;

                // Get the aggregated evidence for the policy to use
                var evidence = httpContext.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var ev)
                    ? ev as AggregatedEvidence
                    : null;

                if (evidence != null)
                {
                    var actionResult = await policy.ExecuteAsync(httpContext, evidence);
                    if (!actionResult.Continue)
                        return null; // Response already written by the policy
                }

                // Evidence missing but bot detected — fall through to default block
            }
        }

        // No action policy, policy not found, or evidence missing — default block
        return Results.Json(new
        {
            error = "Access denied",
            policy = _policyName
        }, statusCode: 403);
    }
}
