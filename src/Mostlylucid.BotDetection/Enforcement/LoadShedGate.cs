using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
///     Pre-detection load-shed decision. Extracted from the inline block in
///     <see cref="BotDetectionMiddleware"/> so the atom-orchestrator path
///     (<see cref="BotDetectionAtomMiddleware"/>) can enforce the same shed
///     behaviour without duplicating the logic.
/// </summary>
/// <remarks>
///     Two shed modes carried over verbatim from the legacy inline block:
///     <list type="bullet">
///         <item>
///             High band: skip detection, forward to upstream. Preserves throughput,
///             only reduces CPU / allocation pressure from detection.
///         </item>
///         <item>
///             Critical band: refuse the request with HTTP 503 + Retry-After.
///             Skipping detection alone doesn't save the gateway under critical
///             pressure because each proxied request still consumes connection
///             state + response buffers; the in-flight set is what blows up RSS.
///             503 with Retry-After is the canonical "back off and retry" signal.
///         </item>
///     </list>
///     Gated by <see cref="BotDetectionOptions.LoadShedEnabled"/> which
///     defaults to <c>false</c>. Adaptive shedding was 503-ing real visitors
///     on normal cold-start latency in the legacy path; the shed stays off
///     until a full review. The sensor still observes so the review has data.
/// </remarks>
public sealed class LoadShedGate
{
    private readonly BotDetectionOptions _options;
    private readonly LoadShedDecision? _decision;
    private readonly PipelineLoadSensor? _sensor;
    private readonly ILogger<LoadShedGate> _logger;

    public LoadShedGate(
        IOptions<BotDetectionOptions> options,
        ILogger<LoadShedGate> logger,
        LoadShedDecision? decision = null,
        PipelineLoadSensor? sensor = null)
    {
        _options = options.Value;
        _decision = decision;
        _sensor = sensor;
        _logger = logger;
    }

    /// <summary>
    ///     Decide whether the request should shed. Returns
    ///     <see cref="LoadShedOutcome.Continue"/> when shedding is disabled
    ///     or the sensor / decision are unwired.
    /// </summary>
    /// <param name="context">The active HTTP context. The gate stamps
    /// <c>Items[BotDetectionMiddleware.BotDetectionShedKey]</c>, the
    /// <c>X-StyloBot-Shed</c> response header, and (for Critical) 503 status +
    /// <c>Retry-After</c> before returning a Refuse/Skip outcome.</param>
    /// <param name="loadShedOptions">Per-request LoadShed policy overlay. When
    /// null, falls back to <c>new LoadShedOptions()</c> defaults -- which is
    /// what happens today under the atom-orchestrator path because it has
    /// not yet resolved a per-endpoint <see cref="DetectionPolicy"/>.</param>
    public LoadShedOutcome Evaluate(HttpContext context, LoadShedOptions? loadShedOptions = null)
    {
        if (!_options.LoadShedEnabled || _decision is null || _sensor is null)
            return LoadShedOutcome.Continue;

        var opts = loadShedOptions ?? new LoadShedOptions();

        var seed = context.Connection?.Id?.GetHashCode()
                   ?? context.Request.Path.Value?.GetHashCode()
                   ?? 0;

        // Cheap verdict-cache peek so the shed protects known humans and
        // preferentially drops known bots. At Critical the LoadShedDecision
        // uses the full VisitorClass fraction table regardless.
        var visitorClass = ResolveVisitorClass(context, opts);
        if (!_decision.ShouldShed(visitorClass, opts, seed))
            return LoadShedOutcome.Continue;

        // Mark the request as shed. Observable surface:
        // HttpContext.Items[BotDetectionShedKey] + X-StyloBot-Shed response
        // header + (for refuse) 503 status. Operational state, NOT a log line
        // -- under load we'd write thousands per second.
        context.Items[BotDetectionMiddleware.BotDetectionShedKey] = true;
        context.Response.Headers["X-StyloBot-Shed"] = "1";

        if (_sensor.CurrentBand == LoadBand.Critical)
        {
            // Refuse: don't forward, don't allocate detection state.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers["Retry-After"] = "5";
            // Mark so the visitor's NEXT request doesn't get bot-boosted
            // by stylobot's own 503 (closed-loop feedback).
            context.MarkResponseFromStyloBot();
            _logger.LogDebug("LoadShedGate: refused with 503 (Critical load)");
            return LoadShedOutcome.Refuse503;
        }

        _logger.LogDebug("LoadShedGate: skipping detection (High load)");
        return LoadShedOutcome.SkipDetection;
    }

    /// <summary>
    ///     Resolves the visitor class for the shed decision from the cached
    ///     prior verdict stashed in <c>HttpContext.Items</c>. Reads the prior
    ///     probability and confidence under
    ///     <see cref="SignalKeys.FingerprintPriorProbability"/> and
    ///     <see cref="SignalKeys.FingerprintPriorConfidence"/>, runs them
    ///     through <see cref="ClassGateResolver.Resolve"/> against the
    ///     policy's <see cref="LoadShedOptions.HumanGate"/> /
    ///     <see cref="LoadShedOptions.BotGate"/>. Cold cache returns
    ///     <see cref="VisitorClass.Unknown"/>.
    /// </summary>
    private static VisitorClass ResolveVisitorClass(HttpContext context, LoadShedOptions options)
    {
        double? prob = context.Items.TryGetValue(SignalKeys.FingerprintPriorProbability, out var p) && p is double pd
            ? pd
            : null;
        double? conf = context.Items.TryGetValue(SignalKeys.FingerprintPriorConfidence, out var c) && c is double cd
            ? cd
            : null;
        return ClassGateResolver.Resolve(prob, conf, options.HumanGate, options.BotGate);
    }
}

/// <summary>Outcome of a <see cref="LoadShedGate.Evaluate"/> call.</summary>
public enum LoadShedOutcome
{
    /// <summary>Continue to detection.</summary>
    Continue = 0,

    /// <summary>Skip detection, forward to upstream. High-load band.</summary>
    SkipDetection = 1,

    /// <summary>Return 503 + Retry-After. Critical-load band. Response is
    /// already set by <see cref="LoadShedGate.Evaluate"/>; the middleware
    /// should <c>return</c> without calling next.</summary>
    Refuse503 = 2,
}