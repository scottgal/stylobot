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
        var fpHeader      = TryGet(headers, StyloBotEdgeHeaderNames.IdentityFingerprint);
        var primaryHeader = TryGet(headers, StyloBotEdgeHeaderNames.PrimarySignature);
        var ipHeader      = TryGet(headers, StyloBotEdgeHeaderNames.IpSignature);
        var uaHeader      = TryGet(headers, StyloBotEdgeHeaderNames.UaSignature);

        if (!string.IsNullOrEmpty(fpHeader))
            context.Items[SignalKeys.IdentityFingerprintId] = fpHeader;

        if (!string.IsNullOrEmpty(primaryHeader))
            context.Items[SignalKeys.PrimarySignature] = primaryHeader;

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

        return _next(context);
    }

    private static string? TryGet(IHeaderDictionary headers, string name)
    {
        if (!headers.TryGetValue(name, out var values)) return null;
        // StringValues.ToString() comma-joins multiple values when the same header
        // arrives more than once. Some gateway/YARP/middleware combinations attach
        // X-Bot-Detection-* twice; without this guard the joined "VALUE, VALUE"
        // string ends up in HttpContext.Items and downstream as a primarySig,
        // breaking /dashboard/signature/{sig} URLs and the fingerprint lookup.
        // Take the first non-empty value; subsequent duplicates are harmless.
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (!string.IsNullOrWhiteSpace(v)) return v;
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
