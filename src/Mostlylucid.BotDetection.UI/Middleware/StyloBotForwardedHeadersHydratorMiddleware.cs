using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

        if (TryGet(headers, StyloBotEdgeHeaderNames.IdentityFingerprint) is { } fpId)
            context.Items[SignalKeys.IdentityFingerprintId] = fpId;

        if (TryGet(headers, StyloBotEdgeHeaderNames.PrimarySignature) is { } primary)
            context.Items[SignalKeys.PrimarySignature] = primary;

        return _next(context);
    }

    private static string? TryGet(IHeaderDictionary headers, string name)
    {
        if (!headers.TryGetValue(name, out var values)) return null;
        var v = values.ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v;
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
