using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     The set of inbound/outbound header names the stylobot reverse-proxy edge uses
///     to talk to a downstream dashboard host. The downstream consumer hydrates
///     <see cref="HttpContext.Items"/> from these, so the dashboard renders identity
///     without running detection locally.
/// </summary>
public static class StyloBotEdgeHeaderNames
{
    public const string IdentityFingerprint = "X-Bot-Detection-IdentityFingerprint";
    public const string PrimarySignature = "X-Bot-Detection-PrimarySignature";
    public const string IpSignature = "X-Bot-Detection-IpSignature";
    public const string UaSignature = "X-Bot-Detection-UaSignature";
    public const string Probability = "X-Bot-Detection-Probability";
    public const string Confidence = "X-Bot-Detection-Confidence";
    public const string RiskBand = "X-Bot-Detection-RiskBand";
    public const string BotName = "X-Bot-Detection-BotName";
    public const string RequestId = "X-Bot-Detection-RequestId";
    public const string Result = "X-Bot-Detection-Result";

    public static readonly string[] All =
    [
        IdentityFingerprint, PrimarySignature, IpSignature, UaSignature,
        Probability, Confidence, RiskBand, BotName, RequestId, Result
    ];
}

/// <summary>
///     Anti-spoofing: removes any <c>X-Bot-Detection-*</c> headers from the inbound
///     request before any detection logic reads them. A visitor could otherwise
///     attach those headers and claim to be a different fingerprint / verdict.
///     Mounted as the FIRST piece of stylobot middleware on the public edge.
/// </summary>
public sealed class StyloBotInboundClientHeaderStripperMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;

    public StyloBotInboundClientHeaderStripperMiddleware(
        RequestDelegate next,
        IOptions<BotDetectionOptions> options)
    {
        _next = next;
        _enabled = options.Value.ForwardedHeaders.StripInboundFromClient;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_enabled)
            foreach (var name in StyloBotEdgeHeaderNames.All)
                if (context.Request.Headers.ContainsKey(name))
                    context.Request.Headers.Remove(name);
        return _next(context);
    }
}

/// <summary>
///     Writes the gateway-computed detection result onto the <em>outbound proxied
///     request</em> so a downstream dashboard host (Stylobot.Website,
///     Stylobot.Ui in viewer mode, anything mounting the FOSS dashboard library
///     with no detection of its own) can render identity from headers alone.
///     Mounted between <see cref="BotDetectionMiddlewareExtensions.UseBotDetection"/>
///     and the YARP <c>MapReverseProxy</c> call.
/// </summary>
public sealed class StyloBotForwardedHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly bool _enabled;
    private readonly ILogger<StyloBotForwardedHeadersMiddleware> _logger;

    public StyloBotForwardedHeadersMiddleware(
        RequestDelegate next,
        IOptions<BotDetectionOptions> options,
        ILogger<StyloBotForwardedHeadersMiddleware> logger)
    {
        _next = next;
        _enabled = options.Value.ForwardedHeaders.EmitOnForwardedRequest;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!_enabled) return _next(context);

        // Identity fingerprint id: the load-bearing value -- the downstream view
        // component renders the radar around it.
        var fpId = context.Items.TryGetValue(SignalKeys.IdentityFingerprintId, out var fpObj)
            ? fpObj as string
            : null;
        if (string.IsNullOrEmpty(fpId)
            && context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evObj)
            && evObj is AggregatedEvidence evidence
            && evidence.Signals.TryGetValue(SignalKeys.IdentityFingerprintId, out var sigObj)
            && sigObj is string sigFp)
        {
            fpId = sigFp;
        }

        if (!string.IsNullOrEmpty(fpId))
            context.Request.Headers[StyloBotEdgeHeaderNames.IdentityFingerprint] = fpId;

        // Primary signature: powers the signature-detail drill-through and the
        // SignaturePeriodicityHeatmap / signature card chrome.
        var primarySig = context.Items.TryGetValue(SignalKeys.PrimarySignature, out var psObj)
            ? psObj as string
            : null;
        if (string.IsNullOrEmpty(primarySig)
            && context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var ev2)
            && ev2 is AggregatedEvidence agg2
            && agg2.Signals.TryGetValue(SignalKeys.PrimarySignature, out var psSig)
            && psSig is string psStr)
        {
            primarySig = psStr;
        }

        if (!string.IsNullOrEmpty(primarySig))
            context.Request.Headers[StyloBotEdgeHeaderNames.PrimarySignature] = primarySig;

        // Verdict shape: probability / risk band / bot name -- everything the
        // home card's verdict badge + reason strip want.
        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var ev3)
            && ev3 is AggregatedEvidence aggregated)
        {
            context.Request.Headers[StyloBotEdgeHeaderNames.Probability] =
                aggregated.BotProbability.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Headers[StyloBotEdgeHeaderNames.Confidence] =
                aggregated.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            context.Request.Headers[StyloBotEdgeHeaderNames.RiskBand] = aggregated.RiskBand.ToString();
            context.Request.Headers[StyloBotEdgeHeaderNames.Result] =
                (aggregated.BotProbability > 0.5).ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(aggregated.PrimaryBotName))
                context.Request.Headers[StyloBotEdgeHeaderNames.BotName] = aggregated.PrimaryBotName;
        }

        context.Request.Headers[StyloBotEdgeHeaderNames.RequestId] = context.TraceIdentifier;

        return _next(context);
    }
}

public static class StyloBotEdgeHeadersExtensions
{
    /// <summary>
    ///     Strip <c>X-Bot-Detection-*</c> from inbound visitor requests. Mount as the
    ///     FIRST stylobot middleware (before any detection-side middleware) so a
    ///     spoofed visitor can't claim to be a different fingerprint / verdict.
    /// </summary>
    public static IApplicationBuilder UseStyloBotInboundClientHeaderStrip(this IApplicationBuilder app)
        => app.UseMiddleware<StyloBotInboundClientHeaderStripperMiddleware>();

    /// <summary>
    ///     Emit <c>X-Bot-Detection-*</c> on the outbound proxied request so a
    ///     downstream dashboard host renders identity from headers alone. Mount
    ///     AFTER <see cref="BotDetectionMiddlewareExtensions.UseBotDetection"/>
    ///     and BEFORE the YARP <c>MapReverseProxy</c> call.
    /// </summary>
    public static IApplicationBuilder UseStyloBotForwardedHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<StyloBotForwardedHeadersMiddleware>();
}
