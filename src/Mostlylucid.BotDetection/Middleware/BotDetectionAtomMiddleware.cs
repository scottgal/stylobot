using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;

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
///         Bounded scope for the initial commit: resolve the scoped
///         <see cref="BotDetectionOrchestrator"/>, call
///         <see cref="BotDetectionOrchestrator.DetectAsync"/>, stash the
///         returned <see cref="AggregatedEvidence"/> on
///         <c>HttpContext.Items[BotDetectionMiddleware.AggregatedEvidenceKey]</c>
///         so existing downstream consumers (dashboard broadcast, YARP
///         edge-header emission, endpoint-policy resolver) continue to work,
///         then call the next middleware. Post-detection concerns that
///         <see cref="BotDetectionMiddleware"/> currently owns
///         (action-policy dispatch, honeypot tag override, per-BotType policy
///         fallback, load-shed, license log-only) migrate onto blackboard
///         escalator atoms in subsequent phases; until then a host that flips
///         <c>BotDetection:UseAtomOrchestrator=true</c> runs in observe-only shape
///         (detection populates the dashboard; bots are not blocked).
///     </para>
///     <para>
///         This middleware is registered via <see cref="UseBotDetectionAtoms"/>.
///         The gateway <c>Program.cs</c> reads the config flag and calls
///         <see cref="UseBotDetectionAtoms"/> instead of
///         <see cref="BotDetectionMiddlewareExtensions.UseBotDetection"/> when
///         the flag is on.
///     </para>
/// </remarks>
public sealed class BotDetectionAtomMiddleware
{
    private readonly RequestDelegate _next;

    public BotDetectionAtomMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Orchestrator is scoped-per-request; resolve from the request's own container.
        var orchestrator = context.RequestServices.GetRequiredService<BotDetectionOrchestrator>();

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

        await _next(context);

        // Response-side facts onto the blackboard. Raised AFTER _next so
        // context.Response.StatusCode reflects the final status (upstream
        // 4xx/5xx via YARP, action-policy synthesised codes, throttle 429,
        // etc). This is where the "requests.status_code / dashboard_detections.status_code
        // always = 200" bug is structurally resolved: consumers subscribed to
        // response.status_code on the blackboard see the FINAL code, not a
        // pre-_next snapshot.
        //
        // The orchestrator's SignalSink is per-request-scoped and lives until the
        // BotDetectionOrchestrator instance is disposed by the request scope
        // (BotDetectionOrchestrator.Dispose calls _signalSink.ClearPattern("*")).
        // Raising here is safe.
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