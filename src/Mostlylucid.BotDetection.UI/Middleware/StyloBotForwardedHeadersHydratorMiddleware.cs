using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Hydrates <see cref="HttpContext.Items"/> from <c>X-Bot-Detection-*</c> headers
///     attached by a stylobot reverse-proxy gateway. Mounted by dashboard-only hosts
///     that don't run detection themselves: the gateway computed identity, attached
///     it to the YARP-forwarded request, and this middleware exposes it to view
///     components / tag helpers through the same context.Items keys the in-process
///     detection middleware would have written.
/// </summary>
public sealed class StyloBotForwardedHeadersHydratorMiddleware
{
    private readonly RequestDelegate _next;

    public StyloBotForwardedHeadersHydratorMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        var fpHeader       = TryGet(headers, StyloBotEdgeHeaderNames.IdentityFingerprint);
        var primaryHeader  = TryGet(headers, StyloBotEdgeHeaderNames.PrimarySignature);
        var ipHeader       = TryGet(headers, StyloBotEdgeHeaderNames.IpSignature);
        var uaHeader       = TryGet(headers, StyloBotEdgeHeaderNames.UaSignature);
        var entityIdHeader = TryGet(headers, StyloBotEdgeHeaderNames.EntityId);

        if (!string.IsNullOrEmpty(fpHeader))
            context.Items[SignalKeys.IdentityFingerprintId] = fpHeader;

        if (!string.IsNullOrEmpty(primaryHeader))
            context.Items[SignalKeys.PrimarySignature] = primaryHeader;

        // Entity id: the durable visitor handle. Drives /dashboard/entity/{id}
        // and the EntityAggregateCache key. PrimarySignature stays as a
        // detection signal (the matcher's input) but the URL/cache surface
        // switches to entity id over follow-up PRs.
        if (!string.IsNullOrEmpty(entityIdHeader))
            context.Items[SignalKeys.EntityId] = entityIdHeader;

        // Reconstruct MultiFactorSignatures so DetectionDataExtractor's
        // canonical-signature branch finds the FULL per-factor set the
        // gateway computed, instead of falling through to the SHA256(ip:ua)
        // truncated fallback that the dashboard URL would otherwise use.
        // Without this, /dashboard/signature/<sig> resolves to a primarySig
        // that the matcher never wrote to fingerprint_keys -- the fingerprint
        // radar then renders calibrating forever on the remote-mode host.
        if (!string.IsNullOrEmpty(primaryHeader))
        {
            context.Items[SignalKeys.SignatureMultifactor] = new MultiFactorSignatures
            {
                PrimarySignature = primaryHeader,
                IpSignature      = ipHeader,
                UaSignature      = uaHeader
            };
        }

        // Verdict: the gateway forwards the composed probability / band / type as X-Bot-Detection-*
        // headers. A trust-upstream host runs no local detection, so self-detection surfaces (the
        // "You:" pill on the marketing home, verdict badges) read the visitor's own result from
        // context.Items["BotDetectionResult"]. The hydrator populated identity but never the RESULT,
        // so those surfaces fell back to 0.0% / Unknown for every visitor. Reconstruct a lightweight
        // BotDetectionResult from the forwarded headers so they show the real gateway verdict.
        var probHeader = TryGet(headers, StyloBotEdgeHeaderNames.Probability);
        if (!string.IsNullOrEmpty(probHeader)
            && double.TryParse(probHeader, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var prob))
        {
            var botTypeHeader = TryGet(headers, StyloBotEdgeHeaderNames.BotType);
            BotType? botType = Enum.TryParse<BotType>(botTypeHeader, ignoreCase: true, out var bt) ? bt : null;
            context.Items["BotDetectionResult"] = new BotDetectionResult
            {
                // The home/self surfaces read ConfidenceScore AS the bot probability (existing
                // upstream-trust contract in Home/Index.cshtml), so map the probability header here.
                ConfidenceScore = prob,
                IsBot = prob >= 0.5,
                BotType = botType,
                BotName = TryGet(headers, StyloBotEdgeHeaderNames.BotName),
            };
        }

        return _next(context);
    }

    private static string? TryGet(IHeaderDictionary headers, string name)
    {
        if (!headers.TryGetValue(name, out var values)) return null;
        // Some gateway/YARP/middleware combinations attach X-Bot-Detection-* twice,
        // either as two distinct header lines (StringValues with Count > 1) or as
        // a single line with the value comma-joined per RFC 7230 ("A, A"). Both
        // paths break downstream consumers -- a doubled primarySig produces an
        // unresolvable /dashboard/signature/{sig} URL and the fingerprint lookup
        // returns 404 forever. Take the first non-empty entry; split off the
        // tail after the first ", " to handle the RFC-7230 single-line case.
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (string.IsNullOrWhiteSpace(v)) continue;
            var commaIdx = v.IndexOf(", ", StringComparison.Ordinal);
            return commaIdx > 0 ? v[..commaIdx] : v;
        }
        return null;
    }
}

public static class StyloBotForwardedHeadersHydratorExtensions
{
    /// <summary>
    ///     Mount the hydrator. Pure dashboard-viewer hosts (no AddBotDetection)
    ///     call this so that view components find identity / signature in
    ///     HttpContext.Items as if the detection middleware had populated them.
    /// </summary>
    public static IApplicationBuilder UseStyloBotForwardedHeadersHydrator(this IApplicationBuilder app)
        => app.UseMiddleware<StyloBotForwardedHeadersHydratorMiddleware>();
}
