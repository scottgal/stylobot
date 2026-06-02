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
    private const string DetectorResource = "Mostlylucid.BotDetection.ClientSide.botdetection.js";
    private const string ChallengeResource = "Mostlylucid.BotDetection.ClientSide.challenge.js";

    private static readonly Dictionary<string, (byte[] Bytes, string Etag)> Cache =
        new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    /// <summary>
    ///     Maps the detector-script endpoint. Default path
    ///     <c>/bot-detection/script.js</c> -- matches
    ///     <c>BotDetectionTagHelper.ScriptPath</c> so the tag helper + endpoint
    ///     stay in sync without configuration.
    /// </summary>
    public static IEndpointConventionBuilder MapBotDetectionScript(
        this IEndpointRouteBuilder endpoints,
        string path = "/bot-detection/script.js")
    {
        return endpoints.MapGet(path, ctx => HandleAsync(ctx, DetectorResource))
            .WithName("BotDetectionScript")
            .WithDisplayName("StyloBot client-side detection script")
            .AllowAnonymous();
    }

    /// <summary>
    ///     Maps the PoW-challenge solver endpoint. Default path
    ///     <c>/bot-detection/challenge.js</c> -- referenced by the
    ///     proof-of-work HTML page emitted by <c>ChallengeActionPolicy</c>.
    ///     Same cache/ETag contract as the detector script.
    /// </summary>
    public static IEndpointConventionBuilder MapBotDetectionChallengeScript(
        this IEndpointRouteBuilder endpoints,
        string path = "/bot-detection/challenge.js")
    {
        return endpoints.MapGet(path, ctx => HandleAsync(ctx, ChallengeResource))
            .WithName("BotDetectionChallengeScript")
            .WithDisplayName("StyloBot PoW challenge solver")
            .AllowAnonymous();
    }

    private static async Task HandleAsync(HttpContext ctx, string resourceName)
    {
        var entry = LoadIfNeeded(resourceName);
        if (entry is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        var (bytes, etag) = entry.Value;

        // Honour If-None-Match -- browsers re-validate cheaply on every page hit.
        var ifNoneMatch = ctx.Request.Headers["If-None-Match"].FirstOrDefault();
        if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == etag)
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = etag;
            return;
        }

        // Direct response write (not Results.File(byte[])) -- the latter
        // conflicted with pre-set ETag headers under BotDetectionMiddleware
        // and produced 200 with empty body. Lesson logged in the original
        // commit (480b790e).
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/javascript; charset=utf-8";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers["Cache-Control"] = "public, max-age=3600, must-revalidate";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
    }

    private static (byte[] Bytes, string Etag)? LoadIfNeeded(string resourceName)
    {
        if (Cache.TryGetValue(resourceName, out var hit)) return hit;
        lock (Lock)
        {
            if (Cache.TryGetValue(resourceName, out hit)) return hit;
            using var stream = typeof(BotDetectionScriptEndpointExtensions).Assembly
                .GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            // Strong ETag: SHA-256 of content, hex-truncated for compactness.
            // Stable per build; changes on every script update.
            var hash = SHA256.HashData(bytes);
            var etag = "\"" + Convert.ToHexString(hash, 0, 8) + "\"";
            Cache[resourceName] = (bytes, etag);
            return (bytes, etag);
        }
    }
}
