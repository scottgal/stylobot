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

        // Diagnostic: surface what the hydrator saw on the inbound request so curl
        // can verify the gateway -> website handoff without rebuild cycles. Set as
        // a response header (no body / no PII). Remove once the YourDetection pill
        // verifies green on staging.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Sb-Hydrator-Saw"] =
                $"fp={(string.IsNullOrEmpty(fpHeader) ? "-" : "y")};" +
                $"primary={(string.IsNullOrEmpty(primaryHeader) ? "-" : primaryHeader)};" +
                $"ip={(string.IsNullOrEmpty(ipHeader) ? "-" : "y")};" +
                $"ua={(string.IsNullOrEmpty(uaHeader) ? "-" : "y")};" +
                $"entity={(string.IsNullOrEmpty(entityIdHeader) ? "-" : entityIdHeader)}";
            return Task.CompletedTask;
        });

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
