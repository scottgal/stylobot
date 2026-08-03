using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
///     Evaluates whether a response is eligible for the content-cache plane.
///     Called inside the interceptor transform callback, AFTER the upstream has
///     written response headers but BEFORE the body is stored.
///     Fail-open: any uncertainty or evaluation error → cacheable.
/// </summary>
public static class CacheabilityEvaluator
{
    /// <summary>
    ///     Returns true when the response is eligible for caching.
    ///     Rejects: non-2xx status, 206 Partial, streamed,
    ///     Cache-Control: no-store|private, Set-Cookie header,
    ///     and request carrying auth/session cookies.
    /// </summary>
    public static bool IsCacheable(HttpContext context, string? responseContentType = null)
    {
        var response = context.Response;

        // Non-2xx or partial content → never cache.
        var status = response.StatusCode;
        if (status is < 200 or >= 300 || status == 206)
            return false;

        // Streaming / not yet started → can't buffer.
        if (response.HasStarted && response.Body is not { CanSeek: true })
            return false;

        // Cache-Control: no-store or private → respect origin directive.
        var cacheControl = response.Headers.CacheControl.ToString();
        if (cacheControl.Contains("no-store", StringComparison.OrdinalIgnoreCase)
            || cacheControl.Contains("private", StringComparison.OrdinalIgnoreCase))
            return false;

        // Set-Cookie → personalised response, never cache.
        if (response.Headers.ContainsKey(HeaderNames.SetCookie))
            return false;

        // Request carries authentication cookies → response may be user-specific.
        if (HasAuthCookies(context.Request))
            return false;

        // Content-Type gate: only cache text/html (not JSON, binary, etc.).
        // If no content type is set yet (interceptor pre-flush), defer to the
        // caller to pass the actual Content-Type.
        var contentType = responseContentType ?? response.ContentType;
        if (contentType is { Length: > 0 }
            && !contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static bool HasAuthCookies(HttpRequest request)
    {
        foreach (var cookie in request.Cookies)
        {
            var name = cookie.Key;
            // Common auth/session cookie name patterns.
            if (name.StartsWith(".AspNet.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("__Host-", StringComparison.Ordinal)
                || name.StartsWith("__Secure-", StringComparison.Ordinal)
                || name.Equals("ASP.NET_SessionId", StringComparison.OrdinalIgnoreCase)
                || name.Equals("stylobot-dashboard-auth", StringComparison.OrdinalIgnoreCase) // dashboard auth is a view cookie but signals personalised UI
                || name.Contains("auth", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
