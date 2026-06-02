using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     Serves the embedded <c>botdetection.js</c> resource at a stable URL so the
///     <see cref="BotDetectionTagHelper"/> can reference it via <c>&lt;script src="..."&gt;</c>
///     instead of inlining a separate twin script. Three knock-on benefits:
///     <list type="bullet">
///         <item>CSP-friendly: ships under <c>script-src 'self'</c> with a nonce,
///               which inline-only scripts cannot do on ~15% of enterprise targets.</item>
///         <item>Cacheable: served with a content-hash ETag so browsers re-validate
///               instead of re-downloading the full script every page hit.</item>
///         <item>Reviewable: the artifact ON THE WIRE is the artifact in the repo --
///               no string interpolation gap between source and runtime.</item>
///     </list>
/// </summary>
public static class BotDetectionScriptEndpointExtensions
{
    private const string ResourceName = "Mostlylucid.BotDetection.ClientSide.botdetection.js";
    private static byte[]? _bytes;
    private static string? _etag;
    private static readonly object Lock = new();

    /// <summary>
    ///     Maps the script endpoint. Default path <c>/bot-detection/script.js</c> --
    ///     matches the <c>BotDetectionTagHelper.ScriptPath</c> default so the tag
    ///     helper and endpoint stay in sync without configuration.
    /// </summary>
    public static IEndpointConventionBuilder MapBotDetectionScript(
        this IEndpointRouteBuilder endpoints,
        string path = "/bot-detection/script.js")
    {
        return endpoints.MapGet(path, HandleAsync)
            .WithName("BotDetectionScript")
            .WithDisplayName("StyloBot client-side detection script")
            .AllowAnonymous();
    }

    private static async Task HandleAsync(HttpContext ctx)
    {
        var (bytes, etag) = LoadIfNeeded();
        if (bytes is null || etag is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Honour If-None-Match -- browsers re-validate cheaply on every page hit.
        var ifNoneMatch = ctx.Request.Headers["If-None-Match"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = etag;
            return;
        }

        // Set headers + body directly. The earlier draft returned
        // Results.File(byte[]); that overload sometimes fights pre-set ETag/
        // Cache-Control headers (overlap with its own range-processing
        // headers) and the body landed empty on the wire under
        // BotDetectionMiddleware. Direct write removes the ambiguity.
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/javascript; charset=utf-8";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers["Cache-Control"] = "public, max-age=3600, must-revalidate";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static (byte[]? Bytes, string? Etag) LoadIfNeeded()
    {
        if (_bytes is not null && _etag is not null) return (_bytes, _etag);
        lock (Lock)
        {
            if (_bytes is not null && _etag is not null) return (_bytes, _etag);
            using var stream = typeof(BotDetectionScriptEndpointExtensions).Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream is null) return (null, null);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _bytes = ms.ToArray();
            // Strong ETag: SHA-256 of content, base64url-truncated for compactness.
            // Stable per build; changes on every script update.
            var hash = SHA256.HashData(_bytes);
            _etag = "\"" + Convert.ToHexString(hash, 0, 8) + "\"";
            return (_bytes, _etag);
        }
    }
}
