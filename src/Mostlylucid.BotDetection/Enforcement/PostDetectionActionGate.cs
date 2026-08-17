using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies.Dispatch.Handlers;
using Mostlylucid.BotDetection.RateLimit;

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
    /// <summary>
    ///     Bucket-policy name for the site-wide safety ceiling (see
    ///     <see cref="BotDetectionOptions.SafetyCeilingRpm"/>). Namespaced so
    ///     it can never collide with a policy-stack Throttle/RateLimit rule
    ///     bucket sharing the same <see cref="ITokenBucketStore"/>.
    /// </summary>
    public const string SafetyCeilingPolicyName = "safety-ceiling";

    /// <summary>
    ///     Every value this class stamps onto <c>evidence.TriggeredActionPolicyName</c> for
    ///     the observability-only fast paths (verified-crawler, registry-client,
    ///     webhook-recognized) must fit in this many characters. <c>TriggeredActionPolicyName</c>
    ///     is free text that downstream event consumers may persist into a fixed-length field
    ///     -- FOSS doesn't know the exact bound any given consumer uses, so 20 is the
    ///     documented, tested contract this class promises to stay under (a field-length
    ///     correctness convention, not any specific schema detail). Pinned by
    ///     <c>PostDetectionActionGateFastPathNameLengthTests</c>.
    /// </summary>
    public const int MaxFastPathActionPolicyNameLength = 20;

    /// <summary>Fast-path stamp: <see cref="IsVerifiedCrawlerMarketingFetch"/>.</summary>
    public const string VerifiedCrawlerFastPathName = "verified-crawler";

    /// <summary>Fast-path stamp: corroborated OCI/Docker registry-client routing.</summary>
    public const string RegistryClientFastPathName = "registry-client";

    /// <summary>Fast-path stamp: corroborated webhook-sender routing.</summary>
    public const string WebhookRecognizedFastPathName = "webhook-recognized";

    private readonly BotDetectionOptions _options;
    private readonly ILogger<PostDetectionActionGate> _logger;
    private readonly ITokenBucketStore? _tokenBucketStore;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly Posture.IDetectionPostureProvider _postureProvider;

    public PostDetectionActionGate(
        IOptions<BotDetectionOptions> options,
        ILogger<PostDetectionActionGate> logger,
        ITokenBucketStore? tokenBucketStore = null,
        IFingerprintStore? fingerprintStore = null,
        Posture.IDetectionPostureProvider? postureProvider = null)
    {
        _options = options.Value;
        _logger = logger;
        _tokenBucketStore = tokenBucketStore;
        _fingerprintStore = fingerprintStore;
        _postureProvider = postureProvider ?? Posture.FullDetectionPostureProvider.Instance;
    }

    /// <summary>
    ///     2026-08-02 fp-cache-current architecture: enforcement's per-BotType fallback
    ///     threshold check must read the SAME live fingerprint score
    ///     (<see cref="Fingerprint.CachedBotProbability"/>) the dashboard headline reads --
    ///     <see cref="Orchestration.Atoms.BotDetectionOrchestrator"/> already wrote this
    ///     request's verdict into the fingerprint cache (power-weighted absorption) before this
    ///     gate runs, so a same-request read-back sees the freshly-absorbed value. Falls back to
    ///     <paramref name="evidence"/>'s own <see cref="AggregatedEvidence.BotProbability"/>
    ///     under exactly three conditions: Identity disabled (store resolves null for every id),
    ///     no fingerprint id resolved this request, or the request is learning-suppressed --
    ///     a learning-suppressed request must score and enforce purely on its own evidence,
    ///     never read another request's absorbed history.
    /// </summary>
    private async Task<double> ResolveEnforcementBotProbabilityAsync(
        HttpContext context, AggregatedEvidence evidence)
    {
        if (_fingerprintStore is null) return evidence.BotProbability;
        if (string.IsNullOrEmpty(evidence.FingerprintId)) return evidence.BotProbability;
        if (context.IsLearningSuppressedByApiKey()) return evidence.BotProbability;

        var fingerprint = await _fingerprintStore.GetFingerprintAsync(
            evidence.FingerprintId, context.RequestAborted);
        return fingerprint?.CachedBotProbability ?? evidence.BotProbability;
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

        // The Internal class's policy is allow (operator 2026-08-16): the product's
        // own plumbing — the dashboard host's compose / hub / REST reads — is a
        // DETECTION CLASS whose policy is never shaped by an endpoint rule. The
        // endpoint override below is exactly how the site→gateway /api/v1 reads got
        // a path-scoped rate-limit action (the trigger set, the class fallback
        // skipped, the 60/min bucket exhausted → 429s → the dashboard's Warming
        // sentinel pages). The class action (logonly/allow via
        // ResolveBotTypeActionPolicy) is the effective policy for internal traffic;
        // the honeypot arm above still wins (behaviour beats identity for negative
        // signals).
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName)
            && evidence.PrimaryBotType == BotType.Internal)
        {
            var apiKeyContext = context.Items.TryGetValue("BotDetection.ApiKeyContext", out var keyCtxObj)
                && keyCtxObj is ApiKeyContext keyCtx
                    ? keyCtx
                    : null;
            var internalAction = ResolveBotTypeActionPolicy(evidence, apiKeyContext);
            if (!string.IsNullOrEmpty(internalAction))
            {
                evidence = evidence with { TriggeredActionPolicyName = internalAction };
                context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
            }
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
                    // Field is bot_probability, NOT a risk band. It was named "risk" and that
                    // label caused three separate misdiagnoses (2026-08-08): readers took a
                    // headless browser's correct bot_probability=1.00 as evidence that risk
                    // measured unusualness rather than activity. RiskBand is an enum
                    // (VeryLow..VeryHigh) and never renders as a decimal -- if you see 1.00
                    // here it is bot-ness. Keep the name matching the value.
                    "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (bot_probability={BotProbability:F2})",
                    evidence.TriggeredActionPolicyName,
                    (_options.ObserveOnly || _postureProvider.ForceLogOnlyPosture) ? " [observe-only shadow]" : "",
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
        if (IsVerifiedCrawlerMarketingFetch(context, evidence))
        {
            // Preserve action observability without invoking a latency-inducing action.
            // TriggeredActionPolicyName is a free-text identifier that downstream event
            // consumers may bound to a fixed-length field -- keep every literal here at
            // 20 chars or under (shortened from "verified-crawler-fast-path", a field-
            // length correctness fix, not a semantic change).
            evidence = evidence with { TriggeredActionPolicyName = VerifiedCrawlerFastPathName };
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
            context.Items["BotDetection.VerifiedCrawlerFastPath"] = true;
            _logger.LogInformation("[ACTION] Verified crawler fast path for {Path}", context.Request.Path);
            return (await GuardWithSafetyCeilingAsync(context, evidence, PostDetectionActionOutcome.PolicyContinued), evidence);
        }

        // Corroborated registry-client benign routing. RegistryClientSensor sets
        // RegistryClientCorroborated ONLY when a registry UA family is corroborated
        // by OCI /v2 protocol behaviour (spoof-guarded), so a legitimate
        // docker/buildx push, containerd pull, or Helm OCI fetch lands here. Its
        // aggregate probability sits above BotThreshold and its BotType is Tool, so
        // the per-BotType fallback below would otherwise route it through
        // BotTypeActionPolicies["Tool"] = "throttle-tools" (HTTP 429 + exponential
        // backoff), tarpitting the push. Detection already ran, scored, logged and
        // learned -- this suppresses ONLY the throttle ACTION, never the detection.
        // Keyed on the corroboration flag, NOT BotType.Tool (curl/python are also
        // Tool and MUST still throttle). Placed AFTER the honeypot + endpoint
        // overrides above (those still win via the TriggeredActionPolicyName guard)
        // and BEFORE the BotType fallback. A rate policy on top is still allowed.
        // Mirrors the verified-crawler sibling above -- same 20-char field-length rule
        // applies (shortened from "registry-client-recognized").
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName)
            && evidence.RegistryClientCorroborated)
        {
            evidence = evidence with { TriggeredActionPolicyName = RegistryClientFastPathName };
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
            context.Items["BotDetection.RegistryClientRecognized"] = true;
            _logger.LogInformation(
                "[ACTION] Registry client recognized (corroborated OCI/Docker v2) for {Path} (bot_probability={BotProbability:F2}) -- benign routing, throttle suppressed",
                context.Request.Path, evidence.BotProbability);
            return (await GuardWithSafetyCeilingAsync(context, evidence, PostDetectionActionOutcome.PolicyContinued), evidence);
        }

        // Corroborated webhook-recognition benign routing. WebhookSensor sets
        // WebhookRecognized ONLY when a recognized webhook sender is corroborated
        // hitting its configured receiver endpoint, so a legitimate webhook delivery
        // (Stripe, GitHub, etc.) lands here. Its aggregate probability can sit above
        // BotThreshold and its BotType can be a friendly-automation bucket, so the
        // per-BotType fallback below would otherwise route it through a
        // throttle/challenge action, tarpitting the delivery. Detection already ran,
        // scored, logged and learned -- this suppresses ONLY the throttle/challenge
        // ACTION, never the detection. Keyed on the corroboration flag, NOT on path
        // or BotType alone (an unrecognized request to the same endpoint must still
        // resolve the normal action). Placed AFTER the honeypot + endpoint overrides
        // above (those still win via the TriggeredActionPolicyName guard) and BEFORE
        // the BotType fallback. Mirrors the registry-client sibling above.
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName)
            && evidence.WebhookRecognized)
        {
            evidence = evidence with { TriggeredActionPolicyName = WebhookRecognizedFastPathName };
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;
            context.Items["BotDetection.WebhookRecognized"] = true;
            _logger.LogInformation(
                "[ACTION] Webhook recognized (corroborated sender) for {Path} (bot_probability={BotProbability:F2}) -- benign routing, throttle suppressed",
                context.Request.Path, evidence.BotProbability);
            return (await GuardWithSafetyCeilingAsync(context, evidence, PostDetectionActionOutcome.PolicyContinued), evidence);
        }

        // Perf: only resolve the live fingerprint score (an async store read) when a policy
        // hasn't already been triggered above -- avoids paying for it on every request when
        // TriggeredActionPolicyName is already set (honeypot / endpoint override / earlier
        // gate), since the fallback branch below is the only consumer.
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
        {
            var enforcementBotProbability = await ResolveEnforcementBotProbabilityAsync(context, evidence);
            if (enforcementBotProbability >= _options.BotThreshold
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
                        // Both numeric fields are bot probabilities, not risk bands -- see the
                        // note on the sibling statement above.
                        "[ACTION] Executing action policy '{ActionPolicy}'{Shadow} for {Path} (bot_probability={BotProbability:F2}, enforcement_bot_probability={EnforcementBotProbability:F2}, type={BotType})",
                        resolvedPolicyName,
                        (_options.ObserveOnly || _postureProvider.ForceLogOnlyPosture) ? " [observe-only shadow]" : "",
                        context.Request.Path, evidence.BotProbability, enforcementBotProbability, evidence.PrimaryBotType);

                    var fallbackResult = await fallbackPolicy.ExecuteAsync(context, evidence, context.RequestAborted);
                    return fallbackResult.Continue
                        ? (PostDetectionActionOutcome.PolicyContinued, evidence)
                        : (PostDetectionActionOutcome.PolicyHandledResponse, evidence);
                }
            }
        }

        return (await GuardWithSafetyCeilingAsync(context, evidence, PostDetectionActionOutcome.NoOverride), evidence);
    }

    /// <summary>
    ///     Site-wide safety-ceiling guard (<see cref="BotDetectionOptions.SafetyCeilingRpm"/>).
    ///     Called at every return site that would otherwise let the request
    ///     through WITHOUT any shaping -- the verified-crawler, corroborated
    ///     registry-client, and recognized-webhook benign arms, plus the
    ///     final no-override fallthrough. Those arms exist so legitimate
    ///     high-volume automation is never throttled below the ceiling;
    ///     this guard is the only thing allowed to shed them, and only once
    ///     an absolute flood exhausts the per-(visitor, endpoint) bucket.
    /// </summary>
    /// <param name="continueOutcome">
    ///     The outcome to return when the request is within the ceiling
    ///     (i.e. the caller's original, un-shaped outcome).
    /// </param>
    /// <returns>
    ///     <paramref name="continueOutcome"/> when admitted;
    ///     <see cref="PostDetectionActionOutcome.PolicyHandledResponse"/>
    ///     (after shaping a 429) when the ceiling is exhausted.
    /// </returns>
    private async Task<PostDetectionActionOutcome> GuardWithSafetyCeilingAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        PostDetectionActionOutcome continueOutcome)
    {
        if (WithinSafetyCeiling(context, evidence, _tokenBucketStore, _options.SafetyCeilingRpm))
            return continueOutcome;

        _logger.LogWarning(
            "[ACTION] Safety ceiling ({Rpm} rpm) exhausted for {Path} -- shedding request that would otherwise have bypassed shaping",
            _options.SafetyCeilingRpm, context.Request.Path);
        await ShapeSafetyCeilingResponseAsync(context).ConfigureAwait(false);
        return PostDetectionActionOutcome.PolicyHandledResponse;
    }

    /// <summary>
    ///     Admits or denies a request against the site-wide safety-ceiling
    ///     token bucket. Keyed on <c>(visitor + ":" + path)</c> so the
    ///     ceiling caps throughput per visitor per endpoint rather than
    ///     globally across the whole site. Guard: only enforces when a
    ///     <see cref="ITokenBucketStore"/> is registered AND <paramref name="rpm"/>
    ///     is greater than zero -- either condition missing means "no
    ///     ceiling", never "deny everything".
    /// </summary>
    private static bool WithinSafetyCeiling(
        HttpContext context,
        AggregatedEvidence evidence,
        ITokenBucketStore? store,
        int rpm)
    {
        if (store is null || rpm <= 0) return true;

        var key = ResolveSafetyCeilingKey(context, evidence);
        return store.TryConsume(
            SafetyCeilingPolicyName,
            key,
            capacity: rpm,
            refillRatePerMinute: rpm);
    }

    /// <summary>
    ///     Stable per-(visitor, endpoint) bucket key. Prefers the
    ///     fingerprint (canonical FOSS visitor identity, mirrors
    ///     <see cref="Policies.Dispatch.Handlers.RateLimitActionHandler"/>'s
    ///     visitor-key resolution); falls back to the remote IP; falls back
    ///     to a literal <c>"anon"</c> so the bucket is still selected.
    /// </summary>
    private static string ResolveSafetyCeilingKey(HttpContext context, AggregatedEvidence evidence)
    {
        var visitor = evidence.Signals.TryGetValue(SignalKeys.PrimarySignature, out var sigObj)
            && sigObj is string sig
            && !string.IsNullOrEmpty(sig)
                ? sig
                : context.Connection?.RemoteIpAddress?.ToString();

        if (string.IsNullOrEmpty(visitor))
            visitor = "anon";

        return visitor + ":" + context.Request.Path;
    }

    /// <summary>
    ///     Shapes the 429 response for an exhausted safety-ceiling bucket.
    ///     Mirrors the shaping in <see cref="ThrottleActionHandler"/> /
    ///     <see cref="RateLimitActionHandler"/> so a safety-ceiling shed
    ///     looks identical, on the wire, to every other token-bucket 429.
    /// </summary>
    private static async Task ShapeSafetyCeilingResponseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        // Closed-loop feedback gate: mark so the visitor's NEXT request
        // doesn't get bot-boosted by stylobot's own 429 response.
        context.MarkResponseFromStyloBot();
        context.Response.Headers["Retry-After"] = "1";
        context.Response.Headers[BlockActionHandler.PolicyHeader] = $"rule-{SafetyCeilingPolicyName}";
        context.Response.ContentType = "application/json";

        await context.Response
            .WriteAsync(
                $$"""{"error":"Too many requests","retryAfter":1,"policy":"{{SafetyCeilingPolicyName}}"}""",
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private bool IsVerifiedCrawlerMarketingFetch(HttpContext context, AggregatedEvidence evidence)
    {
        var fastPath = _options.VerifiedCrawlerFastPath;
        if (fastPath.MarketingHosts.Count == 0 ||
            (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) ||
            !evidence.Signals.TryGetValue(SignalKeys.FriendlyIpVerified, out var trusted) || trusted is not true)
            return false;

        // RequestScope is stamped by DomainNormalizer and prefers gateway-validated TLS SNI;
        // it is deliberately not a direct read of the client-controlled Host header.
        if (!context.Items.TryGetValue(HttpContextItemKeys.RequestScope, out var scopeValue) ||
            scopeValue is not RequestScope scope ||
            !fastPath.MarketingHosts.Contains(scope.Host, StringComparer.OrdinalIgnoreCase))
            return false;

        var path = context.Request.Path.Value ?? "/";
        return !fastPath.ExcludedPathPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
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
        // Per-key action-policy override (ApiKeyConfig.ActionPolicyName — e.g.
        // "logonly" for a monitoring/debug key): the KEY defines its own
        // enforcement posture. Keys without an override keep the standard
        // enforcement — no blanket exemption. The field was documented and
        // plumbed into ApiKeyContext but never consulted here; wired per
        // operator directive 2026-08-14.
        if (apiKeyContext is not null && !string.IsNullOrEmpty(apiKeyContext.ActionPolicyName))
            return apiKeyContext.ActionPolicyName;

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
        // ForceLogOnlyPosture is the host-posture seam's equivalent of ObserveOnly (e.g. a
        // license-expiry log-only state) -- same shadow mechanism, different trigger.
        if (!_options.ObserveOnly && !_postureProvider.ForceLogOnlyPosture) return resolved;
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
