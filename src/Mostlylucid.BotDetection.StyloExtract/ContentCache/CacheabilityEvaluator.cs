using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.StyloExtract.Internals;

namespace Mostlylucid.BotDetection.StyloExtract.ContentCache;

/// <summary>Outcome of a cacheability check. <see cref="CacheabilityDecision.Bypass"/> always fails open to origin.</summary>
public enum CacheabilityDecision
{
    Cacheable,
    Bypass,
}

/// <summary>Cacheability verdict plus a human-readable reason (surfaced in the bypass counters / logs).</summary>
public sealed record CacheabilityResult(CacheabilityDecision Decision, string Reason)
{
    public static CacheabilityResult Cacheable() => new(CacheabilityDecision.Cacheable, string.Empty);

    public static CacheabilityResult Bypass(string reason) => new(CacheabilityDecision.Bypass, reason);
}

/// <summary>
///     Encodes the content-cache "never cache" rules. Evaluated on the request (before lookup) and
///     again on the captured response (before publish). A bypass is always a fail-open to origin —
///     the policy returns <c>Allowed</c> and the pipeline continues to the endpoint.
/// </summary>
public sealed class CacheabilityEvaluator
{
    private const string AuthorizationHeader = "Authorization";
    private const string CookieHeader = "Cookie";
    private const string SetCookieHeader = "Set-Cookie";
    private const string VaryHeader = "Vary";
    private const string CacheControlHeader = "Cache-Control";
    private const string TransferEncodingHeader = "Transfer-Encoding";

    /// <summary>
    ///     Request-side rules: only GET; no Authorization; no cookies (cookie-specific responses must
    ///     never be shared). Anything else bypasses.
    /// </summary>
    public CacheabilityResult EvaluateRequest(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method)) return CacheabilityResult.Bypass("non-GET");
        if (request.Headers.ContainsKey(AuthorizationHeader)) return CacheabilityResult.Bypass("authenticated (Authorization)");
        if (request.Headers.ContainsKey(CookieHeader)) return CacheabilityResult.Bypass("cookie-specific");
        return CacheabilityResult.Cacheable();
    }

    /// <summary>
    ///     Response-side rules, checked on the captured response BEFORE publishing an entry:
    ///     partial (206), errors (&gt;=400), Set-Cookie, Vary: Cookie, Cache-Control no-store/private,
    ///     and streamed (chunked) bodies are never cached.
    /// </summary>
    public CacheabilityResult EvaluateResponse(HttpResponse response)
    {
        if (response.StatusCode == StatusCodes.Status206PartialContent) return CacheabilityResult.Bypass("partial (206)");
        if (response.StatusCode >= StatusCodes.Status400BadRequest) return CacheabilityResult.Bypass($"error ({response.StatusCode})");
        if (response.Headers.ContainsKey(SetCookieHeader)) return CacheabilityResult.Bypass("set-cookie");
        if (ContainsDirective(response.Headers[VaryHeader].ToString(), "cookie"))
            return CacheabilityResult.Bypass("Vary: Cookie");

        var cacheControl = response.Headers[CacheControlHeader].ToString();
        if (ContainsDirective(cacheControl, "no-store") || ContainsDirective(cacheControl, "private"))
            return CacheabilityResult.Bypass("Cache-Control no-store/private");

        // HTTP/2 responses don't carry Transfer-Encoding: chunked (DATA frames instead), so this is a
        // best-effort guard, not a guarantee — buffering is bounded by MaxEntryBytes either way.
        if (response.Headers[TransferEncodingHeader].ToString().Contains("chunked", StringComparison.OrdinalIgnoreCase))
            return CacheabilityResult.Bypass("streamed (chunked)");

        return CacheabilityResult.Cacheable();
    }

    /// <summary>
    ///     Compatibility helper used by the legacy <c>content-cache</c> / <c>extract-markdown</c>
    ///     policy classes: one-shot request + response cacheability check. When
    ///     <paramref name="contentType"/> is supplied the captured response must also be HTML —
    ///     the interceptor only processes HTML bodies.
    /// </summary>
    public static bool IsCacheable(HttpContext context, string? contentType = null)
    {
        var evaluator = new CacheabilityEvaluator();
        if (evaluator.EvaluateRequest(context.Request).Decision != CacheabilityDecision.Cacheable)
            return false;
        if (contentType is not null && !ResponseBodyCapture.IsHtmlContentType(context.Response.ContentType))
            return false;
        return evaluator.EvaluateResponse(context.Response).Decision == CacheabilityDecision.Cacheable;
    }

    /// <summary>Splits a header value into directive tokens and tests <paramref name="name"/> (case-insensitive, suffix-safe).</summary>
    private static bool ContainsDirective(string headerValue, string name)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return false;
        foreach (var part in headerValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directive = part.Contains('=')
                ? part[..part.IndexOf('=')].Trim()
                : part;
            if (directive.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
