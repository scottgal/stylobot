using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Detection middleware. Runs the request through
///     <see cref="BotDetectionOrchestrator"/>'s wave-orchestrated detector
///     atoms and dispatches enforcement via the extracted gate services
///     (<see cref="LoadShedGate"/> pre-detection; <see cref="PolicyDispatchGate"/> /
///     <see cref="PostDetectionActionGate"/> / <see cref="BlockResponseGate"/> /
///     <see cref="ResponsePiiMaskGate"/> post-detection).
/// </summary>
public sealed class BotDetectionMiddleware
{
    public const string AggregatedEvidenceKey = "BotDetection.AggregatedEvidence";
    public const string BotDetectionResultKey = AggregatedEvidenceKey;
    public const string IsBotKey = "BotDetection.IsBot";
    public const string BotProbabilityKey = "BotDetection.BotProbability";
    public const string BotConfidenceKey = "BotDetection.BotConfidence";
    public const string BotTypeKey = "BotDetection.BotType";
    public const string BotNameKey = "BotDetection.BotName";
    public const string BotCategoryKey = "BotDetection.BotCategory";
    public const string DetectionReasonsKey = "BotDetection.DetectionReasons";
    public const string PolicyNameKey = "BotDetection.PolicyName";
    public const string PolicyActionKey = "BotDetection.PolicyAction";
    public const string BotDetectionShedKey = "BotDetection.Shed";
    public const string ResponseFromUpstreamKey = "BotDetection.ResponseFromUpstream";
    public const string TestModeEphemeralKey = "BotDetection.TestModeEphemeral";
    public const string DetectionConfidenceKey = "BotDetection.DetectionConfidence";

    private readonly RequestDelegate _next;
    private readonly LoadShedGate _loadShedGate;
    private readonly PolicyDispatchGate _policyDispatchGate;
    private readonly PostDetectionActionGate _postDetectionActionGate;
    private readonly BlockResponseGate _blockResponseGate;
    private readonly ResponsePiiMaskGate _piiMaskGate;

    public BotDetectionMiddleware(
        RequestDelegate next,
        LoadShedGate loadShedGate,
        PolicyDispatchGate policyDispatchGate,
        PostDetectionActionGate postDetectionActionGate,
        BlockResponseGate blockResponseGate,
        ResponsePiiMaskGate piiMaskGate)
    {
        _next = next;
        _loadShedGate = loadShedGate;
        _policyDispatchGate = policyDispatchGate;
        _postDetectionActionGate = postDetectionActionGate;
        _blockResponseGate = blockResponseGate;
        _piiMaskGate = piiMaskGate;
    }

    public async Task InvokeAsync(HttpContext context, IActionPolicyRegistry actionPolicyRegistry)
    {
        var requestStartTicks = Stopwatch.GetTimestamp();

        // Stamp (Domain, Host) on HttpContext.Items[RequestScope] before anything in this
        // request -- FingerprintMatchAtom (inside DetectAsync below), any DetectionBroadcastMiddleware
        // wrapping this middleware (UseStyloBot ordering), and the upstream app -- can read it.
        // DomainNormalizer.Resolve caches on ctx.Items (checked first-line), so this is a cheap
        // idempotent re-read on hosts where the wrapping DetectionBroadcastMiddleware already
        // stamped it; it's the ONLY stamp on detection-only hosts with no dashboard middleware.
        // Resolved via GetService, not a constructor/method dependency, so hosts that predate
        // this fix and never registered AddBotDetection's DomainNormalizer safety net degrade to
        // RequestScope.Unknown instead of throwing.
        context.RequestServices.GetService<Mostlylucid.BotDetection.Domains.DomainNormalizer>()
            ?.Resolve(context);

        var shedOutcome = _loadShedGate.Evaluate(context);
        if (shedOutcome == LoadShedOutcome.Refuse503)
            return;

        var orchestrator = context.RequestServices.GetRequiredService<BotDetectionOrchestrator>();

        if (shedOutcome == LoadShedOutcome.SkipDetection)
        {
            orchestrator.SignalSink.Raise("request.shed:true", context.TraceIdentifier);
            await _next(context);
            EmitResponseSignals(context, orchestrator, requestStartTicks);
            return;
        }

        var evidence = await orchestrator.DetectAsync(context, context.RequestAborted);
        context.Items[AggregatedEvidenceKey] = evidence;

        var dispatchOutcome = await _policyDispatchGate.EvaluateAsync(context, evidence);
        if (dispatchOutcome == PolicyDispatchResult.Handled)
        {
            EmitResponseSignals(context, orchestrator, requestStartTicks);
            return;
        }

        var (postOutcome, mutated) = await _postDetectionActionGate.EvaluateAsync(
            context, evidence, actionPolicyRegistry);
        evidence = mutated;
        if (postOutcome == PostDetectionActionOutcome.PolicyHandledResponse)
        {
            EmitResponseSignals(context, orchestrator, requestStartTicks);
            return;
        }

        var policyAllowed = context.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey);
        if (postOutcome == PostDetectionActionOutcome.NoOverride && !policyAllowed)
        {
            var blockOutcome = await _blockResponseGate.HandleAsync(context, evidence);
            if (blockOutcome == BlockResponseOutcome.Blocked)
            {
                EmitResponseSignals(context, orchestrator, requestStartTicks);
                return;
            }
        }

        _piiMaskGate.MaybeAutoApplyMaliciousMask(context, evidence);
        await _piiMaskGate.InvokeNextAsync(context, _next);

        EmitResponseSignals(context, orchestrator, requestStartTicks);
    }

    private static void EmitResponseSignals(HttpContext context, BotDetectionOrchestrator orchestrator, long requestStartTicks)
    {
        var sessionId = context.TraceIdentifier;
        var sink = orchestrator.SignalSink;
        sink.Raise($"response.status_code:{context.Response.StatusCode}", sessionId);
        sink.Raise($"response.bytes:{context.Response.ContentLength ?? 0}", sessionId);
        sink.Raise($"response.from_upstream:{context.IsResponseFromUpstream()}", sessionId);

        RecordDegradation(context, context.RequestServices.GetService<DegradationAtom>(), requestStartTicks);
        RecordResponseCoordinator(context, sink, context.RequestServices.GetService<ResponseCoordinator>());
    }

    /// <summary>
    ///     Feeds this response into <see cref="Orchestration.ResponseCoordinator"/> so the NEXT
    ///     request from the same client sees it in <c>ResponseBehaviorAtom</c>'s history read
    ///     (exclusive-404 scan pattern, honeypot hits, auth struggle, rate-limit violations).
    ///     Fire-and-forget, same reasoning as <see cref="Lifecycle.PathLifecycleMiddleware"/>:
    ///     never block the response on a history write. Skips stylobot's own enforcement
    ///     responses for the same reason <see cref="RecordDegradation"/> does -- a self-inflicted
    ///     403/429/challenge status must not read back as bot evidence.
    ///     <para>
    ///     Body-pattern signals (stack-trace / login-failed / rate-limited body markers) are NOT
    ///     populated here -- that needs a response-body-capture wrapper, a separate change with
    ///     its own buffering cost. Status code + path is enough to feed the exclusive-404 /
    ///     honeypot-hit / scan-tier / auth-struggle arms, which read status and path only.
    ///     </para>
    /// </summary>
    internal static void RecordResponseCoordinator(
        HttpContext context, SignalSink sink, ResponseCoordinator? coordinator)
    {
        if (coordinator is null) return;
        if (!context.IsResponseFromUpstream()) return;

        var evidence = context.Items.TryGetValue(AggregatedEvidenceKey, out var raw)
            ? raw as AggregatedEvidence
            : null;

        var signal = new ResponseSignal
        {
            RequestId = context.TraceIdentifier,
            ClientId = ResponseBehaviorAtom.GetClientSignature(context, sink),
            Timestamp = DateTimeOffset.UtcNow,
            StatusCode = context.Response.StatusCode,
            ResponseBytes = context.Response.ContentLength ?? 0,
            Path = context.Request.Path.Value ?? string.Empty,
            Method = context.Request.Method,
            BodySummary = new ResponseBodySummary
            {
                IsPresent = (context.Response.ContentLength ?? 0) > 0,
                Length = (int)Math.Min(context.Response.ContentLength ?? 0, int.MaxValue),
                ContentType = context.Response.ContentType,
                // A6 (review 2026-08-28): ResponseBehaviorAtom's error-harvesting + rate-limit
                // arms read PatternCounts keys that were never populated (no body capture), so
                // those tuned arms never fired. Derive the patterns the arms can know from the
                // status code alone — no body buffering cost.
                MatchedPatterns = BuildResponsePatterns(context.Response.StatusCode)
            },
            RequestBotProbability = evidence?.BotProbability ?? 0.0
        };

        // Fire-and-forget: RecordResponseAsync only enqueues onto the coordinator's own
        // sequential-per-client processing atom, which handles its own errors.
        _ = coordinator.RecordResponseAsync(signal, context.RequestAborted);
    }

    /// <summary>
    ///     Status-derived response patterns (A6, review 2026-08-28). The error-harvesting +
    ///     rate-limit arms of <c>ResponseBehaviorAtom</c> read <c>PatternCounts</c> keys
    ///     ("rate_limit"/"blocked"/"error") that were never populated because the middleware
    ///     captured no response body. Map the status codes the arms can observe without
    ///     buffering the body; body-derived patterns (stack traces, error templates) remain
    ///     out of reach without a capture wrapper and are left unset.
    /// </summary>
    private static IReadOnlyList<string> BuildResponsePatterns(int statusCode) => statusCode switch
    {
        429 => new[] { "rate_limit" },
        401 or 403 => new[] { "blocked" },
        >= 500 => new[] { "error" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    ///     Feeds the real per-request outcome into the passive
    ///     <see cref="DegradationAtom"/> EWMA so <see cref="UpstreamHealthGate"/>
    ///     and the dashboard's site-health card reflect actual upstream 5xx/4xx
    ///     rate instead of the atom sitting permanently unfed. Skips stylobot's
    ///     own enforcement responses (<see cref="StyloBotResponseSignalExtensions.IsResponseFromUpstream"/>
    ///     false) so throttle/block/honeypot codes don't self-poison the gate --
    ///     see <see cref="UpstreamHealthGate"/>'s remarks. <c>internal</c> so the
    ///     test project can pin this contract directly instead of re-deriving it.
    /// </summary>
    internal static void RecordDegradation(HttpContext context, DegradationAtom? degradationAtom, long requestStartTicks)
    {
        if (degradationAtom is null) return;
        if (!context.IsResponseFromUpstream()) return;

        var latencyMs = ResolveUpstreamLatencyMs(context, requestStartTicks);
        degradationAtom.RecordResponse(context.Response.StatusCode, latencyMs, context.Request.Path);
    }

    /// <summary>
    ///     HttpContext.Items key stamped by <c>Stylobot.Gateway.Transforms.UpstreamTimingTransform</c>'s
    ///     response transform. Duplicated as a literal (not a shared constant) because
    ///     this core project cannot reference the Gateway host project; the two must be
    ///     kept in sync by hand -- <c>UpstreamTimingTransformTests</c> / this class's
    ///     tests each pin their own side.
    /// </summary>
    /// <summary>
    ///     HttpContext.Items key for the ORIGINAL TCP peer, stashed by the gateway host
    ///     BEFORE its UseForwardedHeaders middleware runs. The forwarded-headers
    ///     processing overwrites <c>Connection.RemoteIpAddress</c> with the X-Forwarded-For
    ///     value, which would make the InternalTrust peer-only evaluation read the
    ///     forwarded client IP instead of the real peer — the product's own calls (which
    ///     legitimately carry the browser's forwarded headers) then never classify
    ///     Internal (the staging 429 incident, 2026-08-16). The stash preserves the
    ///     peer-only invariant across the middleware ordering; absent (hosts without the
    ///     capture), the evaluation falls back to the (possibly overwritten)
    ///     Connection.RemoteIpAddress as before.
    /// </summary>
    public const string OriginalTcpPeerItemKey = "StyloBot.Peer.OriginalTcpPeer";

    internal const string UpstreamElapsedMsItemKey = "StyloBot.ProxyTiming.UpstreamElapsedMs";

    /// <summary>
    ///     Prefers the gateway-measured upstream RTT (stamped by YARP's response
    ///     transform) when present; falls back to a stopwatch spanning this
    ///     middleware's own <c>_next</c> call for non-gateway topologies (direct
    ///     <c>AddBotDetection</c> embed, sidecar) where no such transform runs.
    /// </summary>
    internal static long ResolveUpstreamLatencyMs(HttpContext context, long requestStartTicks)
    {
        if (context.Items.TryGetValue(UpstreamElapsedMsItemKey, out var stamped) && stamped is double gatewayElapsedMs)
            return (long)Math.Round(gatewayElapsedMs);

        return (long)((Stopwatch.GetTimestamp() - requestStartTicks) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>
    ///     HttpContext.Items key stamped by <c>Stylobot.Gateway.Transforms.UpstreamStatusTransform</c>'s
    ///     response transform. Duplicated as a literal for the same cross-project-boundary
    ///     reason as <see cref="UpstreamElapsedMsItemKey"/>. Stamped only when the request
    ///     actually reached <c>MapReverseProxy</c> and got a real origin response -- absent
    ///     (not zero) is the correct state for honeypot / blocked / throttled traffic, which
    ///     structurally short-circuits before the proxy ever runs.
    /// </summary>
    internal const string UpstreamStatusCodeItemKey = "StyloBot.ProxyTiming.UpstreamStatusCode";

    /// <summary>
    ///     Reads the gateway-stamped real origin status code, or <c>null</c> when this
    ///     request never reached the origin (honeypot, blocked, throttled -- all resolved
    ///     before <c>MapReverseProxy</c> -- or a non-gateway topology with no YARP hop at
    ///     all). Null is the meaningful "no real origin call" signal, not missing data.
    /// </summary>
    internal static int? ResolveUpstreamStatusCode(HttpContext context)
    {
        if (context.Items.TryGetValue(UpstreamStatusCodeItemKey, out var stamped) && stamped is int statusCode)
            return statusCode;

        return null;
    }
}

/// <summary>Response body for block responses (AOT-compatible record).</summary>
internal record BlockedResponse(string Error, string Reason, double RiskScore, string Policy);

/// <summary>Response body for challenge responses (AOT-compatible record).</summary>
internal record ChallengeResponse(string Error, string ChallengeType, double RiskScore);

/// <summary>Extension methods for wiring the detection middleware.</summary>
public static class BotDetectionMiddlewareExtensions
{
    public static IApplicationBuilder UseBotDetection(this IApplicationBuilder app)
    {
        app.UseMiddleware<BotDetectionMiddleware>();

        // Runs AFTER detection (WebhookSensor has already stashed
        // context.Items["sb.webhook.endpoint"] when the shape matched) and wraps the
        // rest of the pipeline, so the upstream status code is only read once _next
        // returns. Registered here (not only in UseStyloBot) so both the
        // dashboard-less "AddBotDetection() + UseBotDetection()" host and UseStyloBot
        // (which calls this method) get outcome recording without a second opt-in.
        return app.UseMiddleware<WebhookOutcomeRecorderMiddleware>();
    }
}