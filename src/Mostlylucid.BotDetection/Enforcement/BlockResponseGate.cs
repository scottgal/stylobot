using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
///     Post-detection block / throttle / challenge response mutation.
///     Extracted from <see cref="BotDetectionMiddleware"/>'s
///     <c>ShouldBlockRequest</c> + <c>HandleBlockedRequest</c> +
///     <c>HandleThrottle</c> so the atom-orchestrator path shapes the same
///     403 / 429 / redirect / challenge / log-only responses.
/// </summary>
/// <remarks>
///     <para>
///         Decision surface (<see cref="ShouldBlock"/>):
///         <list type="bullet">
///             <item>DetectionPolicyAction.Block / Throttle / Challenge on
///             evidence → matching BotBlockAction.</item>
///             <item>Verified good bot early exit → allow.</item>
///             <item>Verified bad bot early exit → block via StatusCode.</item>
///             <item>BotProbability ≥ BotThreshold → block via StatusCode.</item>
///             <item>BlockDetectedBots + BotProbability ≥ MinConfidenceToBlock +
///             BotType not in the Allow* filter → block via StatusCode.</item>
///         </list>
///     </para>
///     <para>
///         Response surface (<see cref="HandleAsync"/>): shapes the response
///         per BotBlockAction:
///         <list type="bullet">
///             <item>StatusCode / Default → JSON 403 (or attribute-overridden code)
///             with a <see cref="BlockedResponse"/> body.</item>
///             <item>Redirect → 302 to Throttling.BlockRedirectUrl.</item>
///             <item>Challenge → 403 + X-Bot-Challenge + JSON body.</item>
///             <item>Throttle → 429 + Retry-After (jitter + risk-scaled per options).</item>
///             <item>LogOnly → returns <see cref="BlockResponseOutcome.Continue"/>
///             so the caller runs _next unchanged.</item>
///         </list>
///     </para>
///     <para>
///         The atom-orchestrator path does not yet resolve a per-endpoint
///         <see cref="Policies.DetectionPolicy"/> or a
///         <see cref="Attributes.BotPolicyAttribute"/>, so decisions fall
///         back to the global options (<see cref="BotDetectionOptions.BotThreshold"/>,
///         <see cref="BotDetectionOptions.MinConfidenceToBlock"/>, etc). Endpoint
///         policy resolution lands with Step 5 of the enforcement extraction.
///     </para>
/// </remarks>
public sealed class BlockResponseGate
{
    private readonly BotDetectionOptions _options;
    private readonly ILogger<BlockResponseGate> _logger;

    public BlockResponseGate(
        IOptions<BotDetectionOptions> options,
        ILogger<BlockResponseGate> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Pure decision — should this request be blocked, and if so with
    ///     what action? No response mutation happens here. See remarks on
    ///     <see cref="BlockResponseGate"/> for the precedence rules.
    /// </summary>
#pragma warning disable CS0618 // BotDetectionOptions.BotThreshold / MinConfidenceToBlock are compatibility fallbacks; the deprecation is orthogonal to atom-orchestrator wire-up.
    public (bool Block, BotBlockAction Action) ShouldBlock(AggregatedEvidence evidence)
    {
        // DetectionPolicyAction from the evidence wins over threshold-based checks.
        if (evidence.PolicyAction == DetectionPolicyAction.Block)
            return (true, BotBlockAction.StatusCode);
        if (evidence.PolicyAction == DetectionPolicyAction.Throttle)
            return (true, BotBlockAction.Throttle);
        if (evidence.PolicyAction == DetectionPolicyAction.Challenge)
            return (true, BotBlockAction.Challenge);

        // Verified bad bot early exit -- always block.
        if (evidence.EarlyExit && evidence.EarlyExitVerdict == EarlyExitVerdict.VerifiedBadBot)
            return (true, BotBlockAction.StatusCode);

        // Never block verified good bots at the middleware level; endpoint
        // filters make the allow/deny call for those.
        if (evidence.EarlyExit && evidence.EarlyExitVerdict == EarlyExitVerdict.VerifiedGoodBot)
            return (false, BotBlockAction.Default);

        // Risk-threshold block. Uses the global BotThreshold as the atom
        // path's stand-in for policy.ImmediateBlockThreshold until endpoint
        // policy resolution lands.
        if (evidence.BotProbability >= _options.BotThreshold)
            return (true, BotBlockAction.StatusCode);

        // App-wide BlockDetectedBots switch. Respects the same allow-list
        // toggles the contributor middleware honours (search engines /
        // social media / monitoring / tools / other verified bots).
        if (_options.BlockDetectedBots
            && evidence.BotProbability >= _options.MinConfidenceToBlock)
        {
            if (BotTypeFilter.IsBotTypeAllowed(
                    evidence.PrimaryBotType,
                    allowSearchEngines: _options.AllowVerifiedSearchEngines,
                    allowSocialMediaBots: _options.AllowSocialMediaBots,
                    allowMonitoringBots: _options.AllowMonitoringBots,
                    allowVerifiedBots: _options.AllowVerifiedSearchEngines,
                    allowTools: _options.AllowTools))
                return (false, BotBlockAction.Default);

            return (true, BotBlockAction.StatusCode);
        }

        return (false, BotBlockAction.Default);
    }
#pragma warning restore CS0618

    /// <summary>
    ///     Combined decision + response-mutation entry point. Evaluates
    ///     <see cref="ShouldBlock"/>, then (when blocked) shapes the
    ///     response and returns <see cref="BlockResponseOutcome.Blocked"/>.
    ///     LogOnly and no-block outcomes return
    ///     <see cref="BlockResponseOutcome.Continue"/> so the caller runs
    ///     <c>_next</c>. Missing <see cref="IActionPolicyRegistry"/> is
    ///     tolerated; the holodeck deflection just skips.
    /// </summary>
    public async Task<BlockResponseOutcome> HandleAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        string policyName = "default")
    {
        var (block, action) = ShouldBlock(evidence);
        if (!block)
            return BlockResponseOutcome.Continue;

        // Holodeck deflection: if the path was pre-tagged as a honeypot or
        // an attack signal fired, prefer routing through the "holodeck"
        // action policy (SimulationPack) before the hard block. Missing
        // registry or missing policy → fall through to the normal block.
        var isHoneypotPath = context.Items.TryGetValue("Holodeck.IsHoneypotPath", out var hpVal) && hpVal is true;
        var hasAttackSignal = evidence.Signals.ContainsKey(SignalKeys.AttackDetected);
        if (isHoneypotPath || hasAttackSignal)
        {
            if (SignatureLookup.Primary(evidence) is { } fp)
                context.Items["Holodeck.Fingerprint"] = fp;

            var registry = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetService<IActionPolicyRegistry>(context.RequestServices);
            var holodeck = registry?.GetPolicy("holodeck");
            if (holodeck is not null)
            {
                _logger.LogInformation(
                    "[HOLODECK] Engaging for {Path} (honeypot={IsHoneypot}, attack={HasAttack})",
                    context.Request.Path, isHoneypotPath, hasAttackSignal);
                var holoResult = await holodeck.ExecuteAsync(context, evidence, context.RequestAborted);
                if (!holoResult.Continue) return BlockResponseOutcome.Blocked;
            }
        }

        var riskScore = evidence.BotProbability;

        _logger.LogWarning(
            "[BLOCK] Request blocked: {Path} policy={Policy} risk={Risk:F2} action={Action}",
            context.Request.Path, policyName, riskScore, action);

        switch (action)
        {
            case BotBlockAction.StatusCode:
            case BotBlockAction.Default:
                context.Response.StatusCode = 403;
                context.MarkResponseFromStyloBot();
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new BlockedResponse(
                    "Access denied",
                    "Request blocked by bot detection",
                    riskScore,
                    policyName));
                return BlockResponseOutcome.Blocked;

            case BotBlockAction.Redirect:
                var redirectUrl = _options.Throttling.BlockRedirectUrl ?? "/blocked";
                context.Response.Redirect(redirectUrl);
                context.MarkResponseFromStyloBot();
                return BlockResponseOutcome.Blocked;

            case BotBlockAction.Challenge:
                context.Response.StatusCode = 403;
                context.MarkResponseFromStyloBot();
                context.Response.Headers["X-Bot-Challenge"] = "required";
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new ChallengeResponse(
                    "Challenge required",
                    _options.Throttling.ChallengeType,
                    riskScore));
                return BlockResponseOutcome.Blocked;

            case BotBlockAction.Throttle:
                await ApplyThrottleAsync(context, riskScore);
                return BlockResponseOutcome.Blocked;

            case BotBlockAction.LogOnly:
                _logger.LogWarning(
                    "Bot detected (shadow mode): path={Path}, risk={Risk:F2}, policy={Policy}",
                    context.Request.Path, riskScore, policyName);
                return BlockResponseOutcome.Continue;

            default:
                return BlockResponseOutcome.Continue;
        }
    }

    /// <summary>
    ///     429 + Retry-After response with optional jitter and risk scaling.
    ///     Ported verbatim from <see cref="BotDetectionMiddleware.HandleThrottle"/>.
    /// </summary>
    private async Task ApplyThrottleAsync(HttpContext context, double riskScore)
    {
        var throttleConfig = _options.Throttling;

        var baseDelay = throttleConfig.BaseDelaySeconds;
        var delay = baseDelay;

        if (throttleConfig.EnableJitter)
        {
            var jitterRange = baseDelay * (throttleConfig.JitterPercent / 100.0);
            var jitterValue = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
            delay = Math.Max(1, baseDelay + (int)jitterValue);
        }

        if (throttleConfig.ScaleByRisk)
            delay = (int)(delay * (1 + riskScore));

        delay = Math.Min(delay, throttleConfig.MaxDelaySeconds);

        context.Response.StatusCode = 429;
        context.MarkResponseFromStyloBot();
        context.Response.Headers["Retry-After"] = delay.ToString();

        if (throttleConfig.EnableJitter)
            context.Response.Headers["X-Retry-Jitter"] = "applied";

        context.Response.ContentType = "application/json";

        if (throttleConfig.DelayResponse)
        {
            var responseDelay = Math.Min(throttleConfig.ResponseDelayMs, 5000);
            if (throttleConfig.EnableJitter)
            {
                var delayJitter = (int)(responseDelay * (throttleConfig.JitterPercent / 100.0));
                responseDelay += Random.Shared.Next(-delayJitter, delayJitter);
                responseDelay = Math.Max(100, responseDelay);
            }

            await Task.Delay(responseDelay, context.RequestAborted);
        }

        var msg = throttleConfig.ThrottleMessage?.Replace("\"", "\\\"") ?? "";
        await context.Response.WriteAsync(
            $$"""{"error":"Too many requests","retryAfter":{{delay}},"message":"{{msg}}"}""");
    }
}

/// <summary>Outcome of a <see cref="BlockResponseGate.HandleAsync"/> call.</summary>
public enum BlockResponseOutcome
{
    /// <summary>Not blocked (or LogOnly). Caller should run <c>_next</c>.</summary>
    Continue = 0,

    /// <summary>Response has been shaped (403 / 429 / redirect / challenge).
    /// Caller should return without calling <c>_next</c>.</summary>
    Blocked = 1
}