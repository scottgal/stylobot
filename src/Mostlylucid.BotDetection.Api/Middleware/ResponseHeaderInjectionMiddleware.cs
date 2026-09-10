using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

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

/// <summary>
///     Writes the <c>X-StyloBot-*</c> verdict headers the SDK clients read in
///     header mode (sdk/node, sdk/caddy, sdk/go). Both values it reports are READ
///     from state the pipeline already resolved -- never re-derived here:
///     <list type="bullet">
///         <item>
///             <c>X-StyloBot-IsBot</c> is the canonical classifier cut,
///             <c>bot_probability &gt;= Classification.BotFloor</c> -- the same cut
///             <c>HttpContext.IsBot()</c>, the dashboard and the stored
///             <c>is_bot</c> use.
///         </item>
///         <item>
///             <c>X-StyloBot-Action</c> is the action policy the pipeline resolved
///             for this request, mapped to the documented
///             Allow/Throttle/Challenge/Block vocabulary.
///         </item>
///     </list>
/// </summary>
public static class ResponseHeaderInjection
{
    public static void InjectHeaders(HttpContext context)
    {
        if (!context.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj)
            || evidenceObj is not AggregatedEvidence evidence)
            return;

        var isBot = evidence.BotProbability >= ResolveBotFloor(context);
        var action = ResolveAction(context, evidence);

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

    /// <summary>
    ///     The configured classifier floor. Read from
    ///     <see cref="ClassificationOptions.BotFloor"/> so this header can never
    ///     disagree with <c>HttpContext.IsBot()</c>, the dashboard or the stored
    ///     <c>is_bot</c> about the same verdict. Falls back to the
    ///     <see cref="ClassificationOptions"/> default when no options are
    ///     registered (unit tests, minimal hosts) -- never to a literal.
    /// </summary>
    private static double ResolveBotFloor(HttpContext context)
        => context.RequestServices?.GetService<IOptions<BotDetectionOptions>>()?.Value.Classification.BotFloor
           ?? new ClassificationOptions().BotFloor;

    /// <summary>
    ///     The action resolved for this request, in the documented
    ///     Allow/Throttle/Challenge/Block vocabulary. Precedence: the named action
    ///     policy the enforcement gate resolved, then a detection-policy decision
    ///     (<see cref="AggregatedEvidence.PolicyAction"/>), then Allow -- nothing
    ///     acted on the request. Deliberately NOT a second opinion derived from
    ///     <see cref="RiskBand"/>: observe-only posture resolves
    ///     <c>throttle-stealth</c> for a high-risk bot, and a risk-band switch would
    ///     advertise Block, telling a downstream enforcer to block traffic this host
    ///     chose not to block.
    /// </summary>
    private static string ResolveAction(HttpContext context, AggregatedEvidence evidence)
    {
        var policyName = evidence.TriggeredActionPolicyName;
        if (!string.IsNullOrEmpty(policyName)
            && context.RequestServices?.GetService<IActionPolicyRegistry>()?.GetPolicy(policyName)
                is { } resolvedPolicy)
            return ToRecommendedAction(resolvedPolicy.Intent);

        return evidence.PolicyAction is { } policyAction
            ? ToRecommendedAction(policyAction)
            : nameof(RecommendedAction.Allow);
    }

    private static string ToRecommendedAction(PolicyIntent intent) => intent switch
    {
        PolicyIntent.Block => nameof(RecommendedAction.Block),
        PolicyIntent.Challenge => nameof(RecommendedAction.Challenge),
        PolicyIntent.Throttle or PolicyIntent.RateLimit => nameof(RecommendedAction.Throttle),
        // Pass (log-only / confirmed human) and Escalate leave the request alone;
        // the header reports what the visitor experiences.
        _ => nameof(RecommendedAction.Allow)
    };

    private static string ToRecommendedAction(DetectionPolicyAction action) => action switch
    {
        DetectionPolicyAction.Block => nameof(RecommendedAction.Block),
        DetectionPolicyAction.Challenge => nameof(RecommendedAction.Challenge),
        DetectionPolicyAction.Throttle => nameof(RecommendedAction.Throttle),
        // Continue / Allow / LogOnly / Escalate*: no interference with the request.
        _ => nameof(RecommendedAction.Allow)
    };
}
