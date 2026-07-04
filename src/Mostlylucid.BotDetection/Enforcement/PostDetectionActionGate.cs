using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
///     Post-detection action-policy override + per-BotType fallback.
///     Extracted from <see cref="BotDetectionMiddleware"/> lines 981-1101
///     verbatim so the atom-orchestrator path applies the same overrides
///     the contributor path already does.
/// </summary>
/// <remarks>
///     Runs three concerns in order after detection completes but before
///     the block-decision gate:
///     <list type="number">
///         <item>
///             <b>Honeypot tag override</b> — if the request was pre-tagged
///             with a <see cref="Honeypot.HoneypotTier"/> ≠ <c>None</c>,
///             forces the evidence's <c>TriggeredActionPolicyName</c> to
///             <see cref="Honeypot.HoneypotResponseActionPolicy.PolicyName"/>
///             so the fake honeypot response fires regardless of the risk
///             score. Only applies when no upstream override (license
///             log-only, verdict-gate, API-key rich context) already set a
///             triggered policy name.
///         </item>
///         <item>
///             <b>Endpoint action-policy override</b> — reads
///             <c>[BotAction("...")]</c> / <c>[BotPolicy(ActionPolicy = "...")]</c>
///             off the matched route's metadata via
///             <see cref="EndpointActionPolicyResolver.ResolveFromEndpoint"/>.
///             Lower precedence than honeypot / license / API-key overrides,
///             higher than the BotType fallback.
///         </item>
///         <item>
///             <b>Execute triggered action policy + BotType fallback</b> —
///             when <c>TriggeredActionPolicyName</c> is set, resolve the
///             <see cref="IActionPolicy"/> (with observe-only shadowing),
///             run it, and honour its <c>Continue</c> flag. When still no
///             policy after honeypot / endpoint overrides but the verdict
///             clears the bot threshold and isn't a verified good bot or
///             whitelisted early exit, resolve via
///             <see cref="BotDetectionOptions.BotTypeActionPolicies"/> or
///             the internal-network overlay (when a valid API-key context
///             is present) and run that; falls back to
///             <see cref="BotDetectionOptions.DefaultActionPolicyName"/>.
///         </item>
///     </list>
///     Continues to run when the atom-orchestrator path lacks a resolved
///     <see cref="Policies.DetectionPolicy"/>; the effective bot threshold
///     collapses to <see cref="BotDetectionOptions.BotThreshold"/> and any
///     per-endpoint override / API-key attribute is skipped.
/// </remarks>
public sealed class PostDetectionActionGate
{
    private readonly BotDetectionOptions _options;
    private readonly ILogger<PostDetectionActionGate> _logger;

    public PostDetectionActionGate(
        IOptions<BotDetectionOptions> options,
        ILogger<PostDetectionActionGate> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Apply honeypot / endpoint / per-BotType overrides. Returns the
    ///     possibly-mutated <paramref name="evidence"/> plus an outcome
    ///     that tells the caller how to proceed:
    ///     <list type="bullet">
    ///         <item><see cref="PostDetectionActionOutcome.NoOverride"/> — no
    ///         action policy fired; caller runs <see cref="BlockResponseGate"/>
    ///         and the normal pipeline.</item>
    ///         <item><see cref="PostDetectionActionOutcome.PolicyHandledResponse"/>
    ///         — an action policy shaped the response and asked the pipeline
    ///         to terminate; caller returns without calling <c>_next</c>.</item>
    ///         <item><see cref="PostDetectionActionOutcome.PolicyContinued"/> —
    ///         an action policy ran and allowed continuation (log-only /
    ///         throttle-stealth). Caller runs <c>_next</c> and skips
    ///         <see cref="BlockResponseGate"/>.</item>
    ///     </list>
    /// </summary>
    public async Task<(PostDetectionActionOutcome Outcome, AggregatedEvidence Evidence)> EvaluateAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        IActionPolicyRegistry actionPolicyRegistry)
    {
        // Honeypot tag override. Preserves the ordering + guard from
        // BotDetectionMiddleware line 997-1011: only sets if no upstream
        // override already claimed a policy.
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName)
            && context.Items.TryGetValue(Honeypot.HoneypotPathTagger.ItemKeyTier, out var postTierVal)
            && postTierVal is Honeypot.HoneypotTier postTier
            && postTier != Honeypot.HoneypotTier.None)
        {
            evidence = evidence with
            {
                TriggeredActionPolicyName = Honeypot.HoneypotResponseActionPolicy.PolicyName
            };
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
        }

        // Per-endpoint action-policy override.
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
        {
            var endpointOverride = EndpointActionPolicyResolver.ResolveFromEndpoint(context);
            if (!string.IsNullOrEmpty(endpointOverride))
            {
                evidence = evidence with { TriggeredActionPolicyName = endpointOverride };
                context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
            }
        }

        // Execute the triggered action policy (if any).
        if (!string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
        {
            var actionPolicy = MaybeShadowForObserveOnly(
                actionPolicyRegistry,
                actionPolicyRegistry.GetPolicy(evidence.TriggeredActionPolicyName));
            if (actionPolicy is not null)
            {
                _logger.LogInformation(
                    "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2})",
                    evidence.TriggeredActionPolicyName,
                    _options.ObserveOnly ? " [observe-only shadow]" : "",
                    context.Request.Path, evidence.BotProbability);

                var actionResult = await actionPolicy.ExecuteAsync(context, evidence, context.RequestAborted);
                return actionResult.Continue
                    ? (PostDetectionActionOutcome.PolicyContinued, evidence)
                    : (PostDetectionActionOutcome.PolicyHandledResponse, evidence);
            }

            _logger.LogWarning(
                "Action policy '{ActionPolicy}' not found in registry, falling back to default handling",
                evidence.TriggeredActionPolicyName);
        }

        // Per-BotType fallback. Only fires when the verdict crosses the bot
        // threshold and the early exit isn't a verified good bot or a
        // whitelisted identity.
#pragma warning disable CS0618 // BotDetectionOptions.BotThreshold is the fallback source until endpoint policy resolution lands under the atom-orchestrator path.
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName)
            && evidence.BotProbability >= _options.BotThreshold
            && evidence.EarlyExitVerdict is not (EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted))
#pragma warning restore CS0618
        {
            var apiKeyContext = context.Items.TryGetValue("BotDetection.ApiKeyContext", out var keyCtxObj)
                && keyCtxObj is ApiKeyContext keyCtx
                    ? keyCtx
                    : null;

            var resolvedPolicyName = ResolveBotTypeActionPolicy(evidence, apiKeyContext)
                                     ?? _options.DefaultActionPolicyName;

            var fallbackPolicy = !string.IsNullOrEmpty(resolvedPolicyName)
                ? MaybeShadowForObserveOnly(
                    actionPolicyRegistry,
                    actionPolicyRegistry.GetPolicy(resolvedPolicyName))
                : null;
            if (fallbackPolicy is not null && resolvedPolicyName is not null)
            {
                evidence = evidence with { TriggeredActionPolicyName = resolvedPolicyName };
                context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;

                _logger.LogInformation(
                    "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (risk={Risk:F2}, type={BotType})",
                    resolvedPolicyName,
                    _options.ObserveOnly ? " [observe-only shadow]" : "",
                    context.Request.Path, evidence.BotProbability, evidence.PrimaryBotType);

                var fallbackResult = await fallbackPolicy.ExecuteAsync(context, evidence, context.RequestAborted);
                return fallbackResult.Continue
                    ? (PostDetectionActionOutcome.PolicyContinued, evidence)
                    : (PostDetectionActionOutcome.PolicyHandledResponse, evidence);
            }
        }

        return (PostDetectionActionOutcome.NoOverride, evidence);
    }

    /// <summary>
    ///     Ported verbatim from
    ///     <see cref="BotDetectionMiddleware"/>: resolve the per-BotType
    ///     action policy for <paramref name="evidence"/>. Internal-network
    ///     bucket wins when both a valid API-key context and an
    ///     <c>ip.is_local</c> signal are present.
    /// </summary>
    private string? ResolveBotTypeActionPolicy(AggregatedEvidence evidence, ApiKeyContext? apiKeyContext)
    {
        if (evidence.PrimaryBotType is null || evidence.PrimaryBotType == BotType.Unknown)
            return null;

        var botTypeName = evidence.PrimaryBotType.Value.ToString();

        if (apiKeyContext is not null
            && _options.InternalNetworkBotTypeActionPolicies.Count > 0
            && evidence.Signals.TryGetValue(SignalKeys.IpIsLocal, out var ipLocalObj)
            && ipLocalObj is bool isLocal
            && isLocal
            && _options.InternalNetworkBotTypeActionPolicies.TryGetValue(botTypeName, out var internalPolicy)
            && !string.IsNullOrEmpty(internalPolicy))
        {
            return internalPolicy;
        }

        if (_options.BotTypeActionPolicies.Count > 0
            && _options.BotTypeActionPolicies.TryGetValue(botTypeName, out var normalPolicy)
            && !string.IsNullOrEmpty(normalPolicy))
        {
            return normalPolicy;
        }

        return null;
    }

    /// <summary>
    ///     Ported verbatim from
    ///     <see cref="BotDetectionMiddleware"/>: when
    ///     <see cref="BotDetectionOptions.ObserveOnly"/> is on, swap any
    ///     non-Pass policy for the built-in <c>logonly</c> shadow so
    ///     shadow-mode installs never take a response-shaping action.
    ///     Missing <c>logonly</c> registration is warned about but the
    ///     original policy runs (better than dropping enforcement
    ///     entirely).
    /// </summary>
    private IActionPolicy? MaybeShadowForObserveOnly(IActionPolicyRegistry registry, IActionPolicy? resolved)
    {
        if (!_options.ObserveOnly) return resolved;
        if (resolved is null) return null;
        if (resolved.Intent == PolicyIntent.Pass) return resolved;
        var shadow = registry.GetPolicy("logonly");
        if (shadow is not null) return shadow;
        _logger.LogWarning(
            "ObserveOnly is set but the 'logonly' policy is not registered. " +
            "Applying the configured policy '{Policy}' instead -- observe-only is NOT effective. " +
            "Re-register the logonly built-in or unset ObserveOnly.",
            resolved.Name);
        return resolved;
    }
}

/// <summary>Outcome of a <see cref="PostDetectionActionGate.EvaluateAsync"/> call.</summary>
public enum PostDetectionActionOutcome
{
    /// <summary>No action policy fired. Caller proceeds to
    /// <see cref="BlockResponseGate"/>.</summary>
    NoOverride = 0,

    /// <summary>An action policy ran and asked the pipeline to terminate;
    /// the response is already shaped. Caller returns without <c>_next</c>.</summary>
    PolicyHandledResponse = 1,

    /// <summary>An action policy ran and allowed the pipeline to continue
    /// (log-only, throttle-stealth). Caller runs <c>_next</c> and skips
    /// <see cref="BlockResponseGate"/>.</summary>
    PolicyContinued = 2
}