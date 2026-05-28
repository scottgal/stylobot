using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
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
    public const string EntityId = "X-Bot-Detection-EntityId";
    public const string Probability = "X-Bot-Detection-Probability";
    public const string Confidence = "X-Bot-Detection-Confidence";
    public const string RiskBand = "X-Bot-Detection-RiskBand";
    public const string BotName = "X-Bot-Detection-BotName";
    public const string RequestId = "X-Bot-Detection-RequestId";
    public const string Result = "X-Bot-Detection-Result";

    public static readonly string[] All =
    [
        IdentityFingerprint, PrimarySignature, IpSignature, UaSignature, EntityId,
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

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled) { await _next(context); return; }

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

        // Verdict-cache-skip paths never run the orchestrator, so neither Items
        // nor AggregatedEvidence carry PrimarySignature. Fall back to a direct
        // compute via MultiFactorSignatureService. The downstream dashboard
        // would otherwise see primary=<absent>, fall through to the
        // SHA256(ip:ua)[..16] fallback in DetectionDataExtractor, and produce
        // /dashboard/signature/<fallback-hex> URLs that can never resolve
        // against fingerprint_keys.
        if (string.IsNullOrEmpty(primarySig))
        {
            var sigService = context.RequestServices.GetService<Dashboard.MultiFactorSignatureService>();
            if (sigService is not null)
            {
                try
                {
                    primarySig = sigService.GenerateSignatures(context).PrimarySignature;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "PrimarySig compute fallback failed");
                }
            }
        }

        if (!string.IsNullOrEmpty(primarySig))
            context.Request.Headers[StyloBotEdgeHeaderNames.PrimarySignature] = primarySig;

        // Entity id: the durable handle the downstream dashboard URLs against.
        // PrimarySignature can rotate (UA / IP shift), the fingerprint id can
        // re-bind, but the entity id is allocated once per actor and persists
        // across both. ResolveEntityAsync is exact-key-fast on the warm path
        // (single SELECT on entity_edges.signature) and allocates on the cold
        // path; cosine-similarity merging is moot here because session vectors
        // for first-encounter requests don't exist yet -- that merge runs
        // later via EntityResolutionService against persisted session data.
        if (!string.IsNullOrEmpty(primarySig))
        {
            var sessionStore = context.RequestServices.GetService<Data.ISessionStore>();
            if (sessionStore is not null)
            {
                try
                {
                    var entityId = await sessionStore.ResolveEntityAsync(primarySig, context.RequestAborted);
                    if (!string.IsNullOrEmpty(entityId))
                    {
                        context.Items[SignalKeys.EntityId] = entityId;
                        context.Request.Headers[StyloBotEdgeHeaderNames.EntityId] = entityId;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Entity-id resolution failed for primarySig={Sig}", primarySig);
                }
            }
        }

        // Verdict-cache skip paths never write IdentityFingerprintId to Items
        // because the orchestrator (and therefore the FingerprintMatchContributor)
        // never ran for the request. Fall back to a direct L1 lookup against
        // fingerprint_keys[primarySig] -- a cheap single-row SELECT on the
        // gateway's local store. Without this, every verdict-cached visitor
        // arrives at the downstream dashboard with no identity.
        if (string.IsNullOrEmpty(fpId) && !string.IsNullOrEmpty(primarySig))
        {
            var reader = context.RequestServices.GetService<IFingerprintReader>();
            if (reader is not null)
            {
                try
                {
                    fpId = await reader.LookupFingerprintIdAsync(primarySig, context.RequestAborted);
                    if (!string.IsNullOrEmpty(fpId))
                        context.Request.Headers[StyloBotEdgeHeaderNames.IdentityFingerprint] = fpId;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "L1 fingerprint lookup failed for primarySig={Sig}", primarySig);
                }
            }
        }

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

        await _next(context);
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
