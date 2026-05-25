using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Post-detection policy gate. Runs after
///     <see cref="BotDetectionMiddleware"/> populates the detection result
///     on <see cref="HttpContext.Items"/>, before any broadcast or
///     response-writing middleware. First-match-wins evaluation against
///     <see cref="DetectionPolicyOptions.Rules"/>; the matched rule's
///     action is dispatched via the existing
///     <see cref="IActionPolicyRegistry"/>.
///     <para>
///     This complements the pre-detection
///     <c>EndpointPolicyMiddleware</c>: the pre-detection gate decides on
///     request shape alone (method, path, host); this gate adds the four
///     detection-output predicates (bot probability, confidence, type,
///     threat band) without changing the rule shape or action vocabulary
///     either operator already knows.
///     </para>
/// </summary>
public sealed class DetectionPolicyMiddleware
{
    public const string ItemKeyMatch = "DetectionPolicy.Match";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<DetectionPolicyOptions> _options;
    private readonly IActionPolicyRegistry _registry;
    private readonly ILogger<DetectionPolicyMiddleware> _logger;

    public DetectionPolicyMiddleware(
        RequestDelegate next,
        IOptionsMonitor<DetectionPolicyOptions> options,
        IActionPolicyRegistry registry,
        ILogger<DetectionPolicyMiddleware> logger)
    {
        _next = next;
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || options.Rules.Count == 0)
        {
            await _next(context);
            return;
        }

        // Pull the detection result the BotDetectionMiddleware placed on the
        // context. If no result exists (this middleware ran before detection,
        // or detection was skipped for a static asset), there's nothing to
        // match the detection-output predicates against -- fall through.
        if (!context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj)
            || evidenceObj is not AggregatedEvidence evidence)
        {
            await _next(context);
            return;
        }

        var inputs = new DetectionInputs(
            BotProbability: evidence.BotProbability,
            Confidence: evidence.Confidence,
            BotType: evidence.PrimaryBotType?.ToString(),
            ThreatBand: evidence.ThreatBand != ThreatBand.None ? evidence.ThreatBand.ToString() : null);

        var match = DetectionPolicyMatcher.FindMatch(options.Rules, context, inputs);
        if (match is null)
        {
            await _next(context);
            return;
        }

        context.Items[ItemKeyMatch] = match;

        var policy = _registry.GetPolicy(match.Action);
        if (policy is null)
        {
            _logger.LogWarning(
                "DetectionPolicy '{Name}' references unregistered action '{Action}'; falling through",
                match.Name ?? "(unnamed)", match.Action);
            await _next(context);
            return;
        }

        var result = await policy.ExecuteAsync(context, evidence, context.RequestAborted)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "DetectionPolicy '{Name}' matched on {Path}: action={Action} continue={Continue} status={Status}",
            match.Name ?? "(unnamed)", context.Request.Path, match.Action, result.Continue, result.StatusCode);

        if (result.Continue)
            await _next(context);
        // else: the action took over the response, short-circuit the pipeline.
    }
}
