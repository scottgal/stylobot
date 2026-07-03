using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Middleware;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Response-side middleware that records every non-asset response into
///     <see cref="IPathLifecycleStore"/> so the honeypot threat scorer can
///     tell "scanner probing a path that used to be real" (high threat)
///     from "scanner probing a path that never existed" (lower threat).
/// </summary>
/// <remarks>
///     <para>
///         Skips static assets at the path filter -- they would dominate
///         the table without contributing signal (CSS hash flips don't
///         carry intent). Fire-and-forget: writes are not awaited so we
///         never block the response.
///     </para>
///     <para>
///         Wired into <c>UseBotDetection()</c> after the detection middleware.
///     </para>
/// </remarks>
public sealed class PathLifecycleMiddleware
{
    private static readonly string[] StaticAssetExtensions =
    [
        ".css", ".js", ".mjs", ".map",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp4", ".webm", ".mp3", ".ogg", ".wav",
        ".pdf"
    ];

    private readonly RequestDelegate _next;
    private readonly IPathLifecycleStore _store;
    private readonly ILogger<PathLifecycleMiddleware> _logger;

    public PathLifecycleMiddleware(
        RequestDelegate next,
        IPathLifecycleStore store,
        ILogger<PathLifecycleMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context);

        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path)) return;
        if (IsStaticAsset(path)) return;

        // Only record what the UPSTREAM app actually served. A status code
        // StyloBot synthesised itself (policy block / challenge / throttle /
        // honeypot / load-shed) says nothing about whether the endpoint exists.
        // Recording our own 403 as a "4xx flip" marks a live path formerly-real
        // and feeds EndpointHistory a threat boost on EVERY subsequent visitor,
        // including real browsers — a self-reinforcing block loop. This is the
        // same status-origin gate the five other status-derived detector arms
        // use; see StyloBotResponseSignalExtensions.
        if (!context.IsResponseFromUpstream()) return;

        var statusCode = context.Response.StatusCode;

        // Source the (domain, host) that owns this response from the same
        // cached RequestScope the detection middleware set on HttpContext.Items.
        // Fallback to RequestScope.Unknown so misconfigured pipelines still
        // record something rather than silently drop the observation.
        var scope = context.Items.TryGetValue(HttpContextItemKeys.RequestScope, out var cached)
                    && cached is RequestScope existing
            ? existing
            : RequestScope.Unknown;

        // Fire-and-forget. The store handles its own errors.
        _ = RecordSafelyAsync(scope, path, statusCode, context.RequestAborted);
    }

    private async Task RecordSafelyAsync(RequestScope scope, string path, int statusCode, CancellationToken ct)
    {
        try
        {
            await _store.RecordResponseAsync(scope, path, statusCode, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PathLifecycle record failed for {Path}", path);
        }
    }

    private static bool IsStaticAsset(string path)
    {
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return false;
        var ext = path.AsSpan(dot);
        foreach (var known in StaticAssetExtensions)
            if (ext.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
