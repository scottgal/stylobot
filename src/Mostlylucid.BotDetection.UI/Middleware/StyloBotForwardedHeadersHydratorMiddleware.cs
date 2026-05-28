using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<StyloBotForwardedHeadersHydratorMiddleware> _logger;

    public StyloBotForwardedHeadersHydratorMiddleware(
        RequestDelegate next,
        ILogger<StyloBotForwardedHeadersHydratorMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Request.Headers;
        var fpHeader = TryGet(headers, StyloBotEdgeHeaderNames.IdentityFingerprint);
        var primaryHeader = TryGet(headers, StyloBotEdgeHeaderNames.PrimarySignature);

        // Diagnostic: log what the gateway actually attached. Trace-level
        // would be cleaner but staging defaults to Information.
        _logger.LogInformation(
            "StyloBot forwarded-headers hydrator: path={Path} fp={Fp} primary={Primary}",
            context.Request.Path.Value,
            fpHeader ?? "<absent>",
            primaryHeader ?? "<absent>");

        if (!string.IsNullOrEmpty(fpHeader))
            context.Items[SignalKeys.IdentityFingerprintId] = fpHeader;

        if (!string.IsNullOrEmpty(primaryHeader))
            context.Items[SignalKeys.PrimarySignature] = primaryHeader;

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
