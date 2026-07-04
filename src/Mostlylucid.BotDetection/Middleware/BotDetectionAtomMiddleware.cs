using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies.Dispatch;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Atom-orchestrator middleware — the target of the atoms-as-detection
///     realignment. Runs the request through <see cref="BotDetectionOrchestrator"/>'s
///     wave orchestrator, which raises signals onto the per-request
///     <c>SignalSink</c> (the blackboard) and triggers the
///     <see cref="SignatureEscalatorAtom"/> tight with that sink.
/// </summary>
/// <remarks>
///     <para>
///         Wired concerns: pre-detection load-shed via <see cref="LoadShedGate"/>
///         (Step 2 of the enforcement extraction). Runs before
///         <see cref="BotDetectionOrchestrator.DetectAsync"/> so a Critical-load
///         request returns 503 without allocating detection state.
///     </para>
///     <para>
///         Still pending under this middleware (each will land as its own
///         gate service in <c>Mostlylucid.BotDetection.Enforcement</c>):
///         license log-only override, honeypot tag override, per-endpoint
///         action-policy override, action-policy dispatch,
///         HandleBlockedRequest / HandleThrottle response mutation, response
///         PII mask. Until those land a host that flips
///         <c>BotDetection:UseAtomOrchestrator=true</c> still runs mostly
///         observe-only (detection populates the dashboard; bots are not
///         blocked except by the load-shed refuse path).
///     </para>
///     <para>
///         Registered via <see cref="UseBotDetectionAtoms"/>. Gateway
///         <c>Program.cs</c> reads the config flag and picks
///         <see cref="UseBotDetectionAtoms"/> vs
///         <see cref="BotDetectionMiddlewareExtensions.UseBotDetection"/>.
///     </para>
/// </remarks>
public sealed class BotDetectionAtomMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LoadShedGate _loadShedGate;
    private readonly PolicyDispatchGate _policyDispatchGate;
    private readonly BlockResponseGate _blockResponseGate;

    public BotDetectionAtomMiddleware(
        RequestDelegate next,
        LoadShedGate loadShedGate,
        PolicyDispatchGate policyDispatchGate,
        BlockResponseGate blockResponseGate)
    {
        _next = next;
        _loadShedGate = loadShedGate;
        _policyDispatchGate = policyDispatchGate;
        _blockResponseGate = blockResponseGate;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Pre-detection load-shed. When the sensor is in the High band we
        // skip detection and forward to upstream; when it's Critical we
        // refuse with 503 + Retry-After (the gate has already stamped the
        // response). Continues transparently when LoadShedEnabled=false.
        var shedOutcome = _loadShedGate.Evaluate(context);
        if (shedOutcome == LoadShedOutcome.Refuse503)
            return;

        // Orchestrator is scoped-per-request; resolve from the request's own container.
        var orchestrator = context.RequestServices.GetRequiredService<BotDetectionOrchestrator>();

        // Skip-detection: still forward to upstream but bypass DetectAsync.
        // Matches BotDetectionMiddleware line 822 semantics: mark shed on the
        // sink for downstream consumers, then run _next unchanged.
        if (shedOutcome == LoadShedOutcome.SkipDetection)
        {
            orchestrator.SignalSink.Raise("request.shed:true", context.TraceIdentifier);
            await _next(context);
            EmitResponseSignals(context, orchestrator);
            return;
        }

        // Run detection through the wave orchestrator. This raises
        // detection.completed / request.risk / request.honeypot signals onto
        // the orchestrator's SignalSink (blackboard), and triggers
        // SignatureEscalatorAtom's OnRequestAnalysisCompleteAsync which
        // handles salience-based escalation into the SignatureResponseCoordinator
        // for this signature.
        var evidence = await orchestrator.DetectAsync(context, context.RequestAborted);

        // Stash the evidence under the canonical items key so existing
        // downstream middleware (DetectionBroadcastMiddleware,
        // StyloBotDashboardMiddleware, YarpExtensions.MapReverseProxy header
        // emission, EndpointPolicyMiddleware, etc.) continue to read the same
        // signal surface they read when the legacy BotDetectionMiddleware ran.
        context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;

        // Policy-stack dispatch. Bridges PolicyAction (Allow / Block / Observe
        // / Tag / Challenge / RateLimit / Throttle) records to the HTTP
        // response BEFORE _next runs. Handled → the dispatcher has already
        // shaped the response so we short-circuit; FallThrough → run _next
        // and emit response signals as normal. Optional dependency: hosts
        // without a PolicyActionDispatcher never trigger the Handled path.
        var dispatchOutcome = await _policyDispatchGate.EvaluateAsync(context, evidence);
        if (dispatchOutcome == PolicyDispatchResult.Handled)
        {
            EmitResponseSignals(context, orchestrator);
            return;
        }

        // Legacy block / throttle / challenge decision + response shaping.
        // Runs AFTER policy-stack dispatch so a policy-stack Allow marker
        // wins (policy dispatcher stamps context.Items[PolicyActionDispatcher
        // .AllowMarkerItemKey] before returning FallThrough on allow-through).
        var policyAllowed = context.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey);
        if (!policyAllowed)
        {
            var blockOutcome = await _blockResponseGate.HandleAsync(context, evidence);
            if (blockOutcome == BlockResponseOutcome.Blocked)
            {
                EmitResponseSignals(context, orchestrator);
                return;
            }
        }

        await _next(context);

        EmitResponseSignals(context, orchestrator);
    }

    // Response-side facts onto the blackboard. Raised AFTER _next so
    // context.Response.StatusCode reflects the final status (upstream
    // 4xx/5xx via YARP, action-policy synthesised codes, throttle 429,
    // etc). This is where the "requests.status_code / dashboard_detections.status_code
    // always = 200" bug is structurally resolved: consumers subscribed to
    // response.status_code on the blackboard see the FINAL code, not a
    // pre-_next snapshot.
    private static void EmitResponseSignals(HttpContext context, BotDetectionOrchestrator orchestrator)
    {
        var sessionId = context.TraceIdentifier;
        var sink = orchestrator.SignalSink;
        sink.Raise($"response.status_code:{context.Response.StatusCode}", sessionId);
        sink.Raise($"response.bytes:{context.Response.ContentLength ?? 0}", sessionId);
        sink.Raise($"response.from_upstream:{context.IsResponseFromUpstream()}", sessionId);
    }
}

/// <summary>
///     Extension methods for wiring the atom-orchestrator middleware.
/// </summary>
public static class BotDetectionAtomMiddlewareExtensions
{
    /// <summary>
    ///     Register <see cref="BotDetectionAtomMiddleware"/> in the pipeline.
    ///     Requires that <see cref="Modules.BotDetectionModuleExtensions.AddBotDetectionModule"/>
    ///     has been called at DI-registration time so
    ///     <see cref="BotDetectionOrchestrator"/> is resolvable.
    /// </summary>
    /// <example>
    ///     <code>
    ///     if (builder.Configuration.GetValue&lt;bool&gt;("BotDetection:UseAtomOrchestrator"))
    ///         app.UseBotDetectionAtoms();
    ///     else
    ///         app.UseBotDetection();
    ///     </code>
    /// </example>
    public static IApplicationBuilder UseBotDetectionAtoms(this IApplicationBuilder app)
    {
        return app.UseMiddleware<BotDetectionAtomMiddleware>();
    }
}