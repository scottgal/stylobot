using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Runs operator-declared endpoint policy rules BEFORE bot detection.
///     If a rule matches, the named action policy is dispatched immediately;
///     block / challenge / redirect short-circuit the pipeline, throttle /
///     logonly continue so detection still observes.
/// </summary>
/// <remarks>
///     <para>
///         Wired by <c>UseBotDetection</c> between the lifecycle middleware
///         and the honeypot tagger. The pipeline order is:
///         <c>AssetHash &gt; EndpointPolicy &gt; PathLifecycle &gt;
///         HoneypotPathTagger &gt; BotDetection</c>.
///     </para>
///     <para>
///         Matched rules write
///         <c>HttpContext.Items["EndpointPolicy.Match"]</c> so the dashboard
///         + downstream observers can attribute the response to the rule.
///     </para>
/// </remarks>
public sealed class EndpointPolicyMiddleware
{
    public const string ItemKeyMatch = "EndpointPolicy.Match";

    private readonly RequestDelegate _next;
    private readonly IEndpointPolicyResolver _resolver;
    private readonly IActionPolicyRegistry _actions;
    private readonly ILogger<EndpointPolicyMiddleware> _logger;

    public EndpointPolicyMiddleware(
        RequestDelegate next,
        IEndpointPolicyResolver resolver,
        IActionPolicyRegistry actions,
        ILogger<EndpointPolicyMiddleware> logger)
    {
        _next = next;
        _resolver = resolver;
        _actions = actions;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var match = _resolver.Match(context);
        if (match is null)
        {
            await _next(context);
            return;
        }

        context.Items[ItemKeyMatch] = match;

        // Fast path: a block rule with an explicit StatusCode short-circuits
        // before the action policy registry. The built-in BlockActionPolicy
        // hard-codes 403; bypassing it lets a rule say "block with 451",
        // "block with 405", "block with 501" cleanly.
        if (match.StatusCode.HasValue
            && string.Equals(match.ActionPolicyName, "block", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = match.StatusCode.Value;
            // Closed-loop feedback gate: mark so the visitor's NEXT request
            // doesn't get bot-boosted by stylobot's own endpoint-policy
            // status code.
            context.MarkResponseFromStyloBot();
            context.Response.Headers.TryAdd("X-StyloBot-EndpointPolicy", match.Reason ?? "block");
            _logger.LogInformation(
                "EndpointPolicy match: {Method} {Path} -> block {Status} ({Reason})",
                context.Request.Method, context.Request.Path,
                match.StatusCode.Value, match.Reason ?? "no reason");
            return;
        }

        var actionPolicy = _actions.GetPolicy(match.ActionPolicyName);
        if (actionPolicy is null)
        {
            _logger.LogWarning(
                "EndpointPolicy rule matched but action policy '{Action}' is not registered. Falling through.",
                match.ActionPolicyName);
            await _next(context);
            return;
        }

        _logger.LogInformation(
            "EndpointPolicy match: {Method} {Path} -> {Action} ({Reason})",
            context.Request.Method,
            context.Request.Path,
            match.ActionPolicyName,
            match.Reason ?? "no reason");

        // Synthetic evidence -- no detection has run yet, so we don't have an
        // AggregatedEvidence. The action policies accept it for context but
        // none of the built-ins NRE on a stub.
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.0,
            Confidence = 0.0,
            RiskBand = RiskBand.Unknown,
            TotalProcessingTimeMs = 0.0,
            TriggeredActionPolicyName = match.ActionPolicyName
        };

        var result = await actionPolicy.ExecuteAsync(context, evidence, context.RequestAborted);

        if (!result.Continue) return;
        await _next(context);
    }
}
