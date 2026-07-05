using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Enforcement;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Policies.Dispatch;

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
        var shedOutcome = _loadShedGate.Evaluate(context);
        if (shedOutcome == LoadShedOutcome.Refuse503)
            return;

        var orchestrator = context.RequestServices.GetRequiredService<BotDetectionOrchestrator>();

        if (shedOutcome == LoadShedOutcome.SkipDetection)
        {
            orchestrator.SignalSink.Raise("request.shed:true", context.TraceIdentifier);
            await _next(context);
            EmitResponseSignals(context, orchestrator);
            return;
        }

        var evidence = await orchestrator.DetectAsync(context, context.RequestAborted);
        context.Items[AggregatedEvidenceKey] = evidence;

        var dispatchOutcome = await _policyDispatchGate.EvaluateAsync(context, evidence);
        if (dispatchOutcome == PolicyDispatchResult.Handled)
        {
            EmitResponseSignals(context, orchestrator);
            return;
        }

        var (postOutcome, mutated) = await _postDetectionActionGate.EvaluateAsync(
            context, evidence, actionPolicyRegistry);
        evidence = mutated;
        if (postOutcome == PostDetectionActionOutcome.PolicyHandledResponse)
        {
            EmitResponseSignals(context, orchestrator);
            return;
        }

        var policyAllowed = context.Items.ContainsKey(PolicyActionDispatcher.AllowMarkerItemKey);
        if (postOutcome == PostDetectionActionOutcome.NoOverride && !policyAllowed)
        {
            var blockOutcome = await _blockResponseGate.HandleAsync(context, evidence);
            if (blockOutcome == BlockResponseOutcome.Blocked)
            {
                EmitResponseSignals(context, orchestrator);
                return;
            }
        }

        _piiMaskGate.MaybeAutoApplyMaliciousMask(context, evidence);
        await _piiMaskGate.InvokeNextAsync(context, _next);

        EmitResponseSignals(context, orchestrator);
    }

    private static void EmitResponseSignals(HttpContext context, BotDetectionOrchestrator orchestrator)
    {
        var sessionId = context.TraceIdentifier;
        var sink = orchestrator.SignalSink;
        sink.Raise($"response.status_code:{context.Response.StatusCode}", sessionId);
        sink.Raise($"response.bytes:{context.Response.ContentLength ?? 0}", sessionId);
        sink.Raise($"response.from_upstream:{context.IsResponseFromUpstream()}", sessionId);
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
        return app.UseMiddleware<BotDetectionMiddleware>();
    }
}