using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Mutates <see cref="HttpRequest.QueryString"/> to inject detection signals
///     after <see cref="BotDetectionMiddleware"/> has run. The rewritten URL is
///     visible to every downstream handler — YARP forwards it verbatim,
///     MVC/minimal-API endpoints see it on <c>Request.Query</c>, and any CDN
///     in the path can key its cache on the URL alone (no <c>Vary</c> needed).
///
///     Register AFTER <c>UseBotDetection</c> and BEFORE <c>MapReverseProxy</c>
///     / <c>MapControllers</c>:
///     <code>
///     app.UseBotDetection();
///     app.UseBotDetectionUrlRewrite();
///     app.MapReverseProxy();
///     </code>
///
///     Disabled by default — flip <c>BotDetection:UrlRewrite:Enabled</c>. See
///     <see cref="UrlRewriteOptions"/> for the full security model.
/// </summary>
public sealed class UrlRewriteSignalsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<BotDetectionOptions> _options;
    private readonly ILogger<UrlRewriteSignalsMiddleware> _logger;

    public UrlRewriteSignalsMiddleware(
        RequestDelegate next,
        IOptions<BotDetectionOptions> options,
        ILogger<UrlRewriteSignalsMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rewriteOpts = _options.Value.UrlRewrite;

        if (UrlSignalProjection.ShouldRewrite(context.Request.Path, rewriteOpts))
        {
            try
            {
                Rewrite(context, rewriteOpts);
            }
            catch (InvalidOperationException ex)
            {
                // Config error (signing requested with no key) — log once per request
                // and let detection continue; never fail the request because of a rewrite.
                _logger.LogWarning(ex, "URL rewrite skipped due to configuration error");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "URL rewrite failed unexpectedly; passing request through unmodified");
            }
        }

        await _next(context);
    }

    private void Rewrite(HttpContext context, UrlRewriteOptions opts)
    {
        var prefix = opts.Prefix ?? "sb_";
        var preserved = new Dictionary<string, StringValues>(StringComparer.Ordinal);

        foreach (var kvp in context.Request.Query)
        {
            // Strip-existing: drop any inbound param whose name uses our prefix.
            // Without this an attacker could pre-populate sb_country=US and slip past
            // signature validation on an upstream that's lenient about which params
            // are signed.
            if (opts.StripExisting && kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            preserved[kvp.Key] = kvp.Value;
        }

        var signingKey = ResolveSigningKey(opts);

        context.AddBotDetectionQueryParams(opts, signingKey, (name, value) =>
        {
            preserved[name] = value;
        });

        context.Request.QueryString = BuildQueryString(preserved);
    }

    private byte[]? ResolveSigningKey(UrlRewriteOptions opts)
    {
        if (!opts.Sign) return null;
        var key = _options.Value.SignatureHashKey;
        return string.IsNullOrEmpty(key) ? null : Encoding.UTF8.GetBytes(key);
    }

    private static QueryString BuildQueryString(IReadOnlyDictionary<string, StringValues> pairs)
    {
        if (pairs.Count == 0) return QueryString.Empty;

        var sb = new StringBuilder("?");
        var first = true;
        foreach (var kvp in pairs)
        {
            foreach (var v in kvp.Value)
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kvp.Key));
                if (v is not null)
                {
                    sb.Append('=');
                    sb.Append(Uri.EscapeDataString(v));
                }
                first = false;
            }
        }
        return new QueryString(sb.ToString());
    }
}

/// <summary>
///     Application-builder helpers for <see cref="UrlRewriteSignalsMiddleware"/>.
/// </summary>
public static class UrlRewriteSignalsMiddlewareExtensions
{
    /// <summary>
    ///     Adds the URL-rewrite middleware. No-op unless <c>BotDetection:UrlRewrite:Enabled</c>
    ///     is true. Register after <c>UseBotDetection</c> and before any downstream
    ///     handler that should see the rewritten query.
    /// </summary>
    public static IApplicationBuilder UseBotDetectionUrlRewrite(this IApplicationBuilder app)
        => app.UseMiddleware<UrlRewriteSignalsMiddleware>();
}
