using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Enforcement;

/// <summary>
///     Post-detection response PII mask wrapper. Extracted from
///     <see cref="BotDetectionMiddleware"/>'s
///     <c>InvokeNextWithResponseMutationAsync</c> +
///     <c>IsResponsePiiMaskActionRequested</c> +
///     <c>EnsureMaliciousFallbackMaskPii</c> +
///     <c>EmitResponsePiiMaskingSkippedSignal</c> +
///     <c>EmitResponsePiiMaskSignals</c> so the atom-orchestrator path
///     applies the same response-body PII redaction.
/// </summary>
/// <remarks>
///     <para>
///         Two entry points:
///         <list type="bullet">
///             <item><see cref="MaybeAutoApplyMaliciousMask"/> — inspects
///             the evidence for a high-confidence malicious verdict and
///             stamps <c>BotDetection.Action = "mask-pii"</c> onto
///             <see cref="HttpContext.Items"/>. Caller invokes this
///             AFTER post-detection action gates run and BEFORE
///             <see cref="InvokeNextAsync"/>.</item>
///             <item><see cref="InvokeNextAsync"/> — wraps <c>_next</c>
///             with a <see cref="SlidingWindowPiiMaskingStream"/> when
///             mask-pii was requested. Falls through to a plain
///             <c>next(context)</c> otherwise, emitting the
///             feature-disabled / service-missing skip signals when the
///             configuration or DI graph can't honour the request.</item>
///         </list>
///     </para>
/// </remarks>
public sealed class ResponsePiiMaskGate
{
    private readonly BotDetectionOptions _options;
    private readonly ILogger<ResponsePiiMaskGate> _logger;

    public ResponsePiiMaskGate(
        IOptions<BotDetectionOptions> options,
        ILogger<ResponsePiiMaskGate> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Safety net for high-confidence malicious traffic that still
    ///     gets to run through <c>_next</c>. Stamps the mask-pii action so
    ///     <see cref="InvokeNextAsync"/> applies redaction. No-op when
    ///     the feature is disabled, mask-pii already requested, or the
    ///     evidence doesn't clear the malicious-bot verdict + thresholds.
    /// </summary>
    public void MaybeAutoApplyMaliciousMask(HttpContext context, AggregatedEvidence evidence)
    {
        if (!_options.ResponsePiiMasking.Enabled ||
            !_options.ResponsePiiMasking.AutoApplyForHighConfidenceMalicious)
            return;

        if (IsResponsePiiMaskActionRequested(context))
            return;

        if (evidence.PrimaryBotType != BotType.MaliciousBot)
            return;

        if (evidence.BotProbability < _options.ResponsePiiMasking.AutoApplyBotProbabilityThreshold ||
            evidence.Confidence < _options.ResponsePiiMasking.AutoApplyConfidenceThreshold)
            return;

        context.Items["BotDetection.Action"] = "mask-pii";
        if (string.IsNullOrEmpty(evidence.TriggeredActionPolicyName))
            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence with
            {
                TriggeredActionPolicyName = "mask-pii"
            };

        _logger.LogWarning(
            "[ACTION] Auto-applied mask-pii for high-confidence malicious request on {Path} " +
            "(bot_probability={BotProbability:F2}, confidence={Confidence:F2})",
            context.Request.Path, evidence.BotProbability, evidence.Confidence);
    }

    /// <summary>
    ///     Runs <paramref name="next"/> with response-body PII masking
    ///     applied when the request was flagged with the mask-pii action.
    ///     Falls through to a plain <c>next(context)</c> otherwise.
    /// </summary>
    public async Task InvokeNextAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsResponsePiiMaskActionRequested(context))
        {
            await next(context);
            return;
        }

        if (!_options.ResponsePiiMasking.Enabled)
        {
            EmitResponsePiiMaskingSkippedSignal(context, "feature-disabled");
            _logger.LogInformation(
                "[ACTION] mask-pii requested for {Path} but BotDetection:ResponsePiiMasking:Enabled is false. Continuing unmodified.",
                context.Request.Path);
            await next(context);
            return;
        }

        var masker = context.RequestServices.GetService<IResponsePiiMasker>();
        if (masker is null)
        {
            EmitResponsePiiMaskingSkippedSignal(context, "service-missing");
            _logger.LogWarning(
                "[ACTION] mask-pii requested for {Path} but no IResponsePiiMasker is registered. Continuing unmodified.",
                context.Request.Path);
            await next(context);
            return;
        }

        // Content length can change after masking. Force chunked/unknown length
        // response and stamp the response-action header.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove("Content-Length");
            context.Response.Headers.TryAdd("X-Bot-Response-Action", "mask-pii");
            return Task.CompletedTask;
        });

        var originalBody = context.Response.Body;
        var maskingStream = new SlidingWindowPiiMaskingStream(
            originalBody,
            masker,
            () => masker.ShouldMaskContent(context.Response.ContentType, context.Response.Headers));

        context.Response.Body = maskingStream;
        try
        {
            await next(context);
            await maskingStream.CompleteAsync(context.RequestAborted);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        EmitResponsePiiMaskSignals(
            context,
            maskingStream.RedactionCount,
            mode: "sliding-window",
            failOpen: maskingStream.FellBackToPassthrough);
    }

    private static bool IsResponsePiiMaskActionRequested(HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetection.Action", out var action) &&
            action is string marker &&
            (marker.Equals("mask-pii", StringComparison.OrdinalIgnoreCase) ||
             marker.Equals("strip-pii", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static void EmitResponsePiiMaskingSkippedSignal(HttpContext context, string reason)
    {
        context.Items["BotDetection.ResponsePiiMasking.Attempted"] = false;
        context.Items["BotDetection.ResponsePiiMasking.Skipped"] = true;
        context.Items["BotDetection.ResponsePiiMasking.SkipReason"] = reason;

        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            var updatedSignals = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase)
            {
                ["response.pii_masking.attempted"] = false,
                ["response.pii_masking.skipped"] = true,
                ["response.pii_masking.skip_reason"] = reason
            };

            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence with
            {
                Signals = updatedSignals
            };
        }
    }

    private static void EmitResponsePiiMaskSignals(
        HttpContext context,
        int redactionCount,
        string mode,
        bool failOpen)
    {
        context.Items["BotDetection.ResponsePiiMasking.Attempted"] = true;
        context.Items["BotDetection.ResponsePiiMasking.RedactionCount"] = redactionCount;
        context.Items["BotDetection.ResponsePiiMasking.Mode"] = mode;
        context.Items["BotDetection.ResponsePiiMasking.FailOpen"] = failOpen;
        context.Items["BotDetection.ResponsePiiMasking.Masked"] = redactionCount > 0;

        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            var updatedSignals = new Dictionary<string, object>(evidence.Signals, StringComparer.OrdinalIgnoreCase)
            {
                ["response.pii_masking.attempted"] = true,
                ["response.pii_masking.masked"] = redactionCount > 0,
                ["response.pii_masking.redaction_count"] = redactionCount,
                ["response.pii_masking.mode"] = mode,
                ["response.pii_masking.fail_open"] = failOpen
            };

            var updatedDetectors = new HashSet<string>(evidence.ContributingDetectors, StringComparer.OrdinalIgnoreCase)
            {
                "ResponsePiiMasker"
            };

            context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence with
            {
                Signals = updatedSignals,
                ContributingDetectors = updatedDetectors
            };
        }
    }
}