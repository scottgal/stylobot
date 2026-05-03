using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Response-side middleware that records ETag / Last-Modified fingerprints for static assets.
///     Must be registered BEFORE BotDetectionMiddleware in the pipeline so it wraps the full
///     request/response cycle. After the inner pipeline completes, reads response headers and
///     calls <see cref="AssetHashStore.RecordHashAsync"/> to detect content changes.
///     Only processes static asset paths (CSS, JS, images, fonts).
///     When a fingerprint change is detected, <see cref="AssetHashStore"/> marks the path stale
///     in <see cref="CentroidSequenceStore"/> so ContentSequenceContributor can suppress
///     false-positive divergence scoring on the NEXT request for the same path.
/// </summary>
public sealed class AssetHashMiddleware(
    RequestDelegate next,
    AssetHashStore assetHashStore,
    ILogger<AssetHashMiddleware> logger)
{
    private static readonly HashSet<string> StaticExtensions =
    [
        ".css", ".js", ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".avif", ".ico"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        var path = context.Request.Path.Value ?? string.Empty;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!StaticExtensions.Contains(ext))
            return;

        var fingerprint = BuildFingerprint(context.Response);
        if (string.IsNullOrEmpty(fingerprint))
            return;

        try
        {
            var changed = await assetHashStore.RecordHashAsync(path, fingerprint, context.RequestAborted);
            if (changed)
                logger.LogDebug("AssetHashMiddleware: fingerprint changed for {Path}", path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AssetHashMiddleware: failed to record hash for {Path}", path);
        }
    }

    private static string BuildFingerprint(HttpResponse response)
    {
        // Prefer strong ETag
        var etag = response.Headers.ETag.ToString();
        if (!string.IsNullOrEmpty(etag))
            return etag;

        // Fallback: Last-Modified + Content-Length composite
        var lastModified = response.Headers["Last-Modified"].ToString();
        var length = response.ContentLength?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(lastModified))
            return $"{lastModified}|{length}";

        return string.Empty;
    }
}
