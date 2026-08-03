using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
///     Builds deterministic, variant-scoped cache keys for the content-cache plane.
///     Every component that reads or writes the cache MUST go through this builder
///     so keys are consistent across <c>ContentCacheActionPolicy</c> and
///     <c>ExtractMarkdownActionPolicy</c>.
/// </summary>
public static class CacheKeyBuilder
{
    /// <summary>
    ///     Build a cache key from the request and policy configuration.
    ///     Format: <c>{representation}|{salt}|{host}|{method}|{normalisedPath}|{selectedQuery}</c>
    /// </summary>
    /// <param name="request">The incoming HTTP request.</param>
    /// <param name="representation">What the cache slot holds, e.g. "html" or "markdown".</param>
    /// <param name="salt">Opaque version salt; changing it invalidates all existing entries.</param>
    /// <param name="allowedQueryKeys">
    ///     Query parameter names to include in the key (case-insensitive).
    ///     Any query param NOT in this set is dropped. Pass null or empty to include none.
    /// </param>
    public static string Build(
        HttpRequest request,
        string representation,
        string salt,
        IReadOnlySet<string>? allowedQueryKeys = null)
    {
        var host = request.Host.Host.ToLowerInvariant();
        var method = request.Method;
        var path = NormalisePath(request.Path.Value);

        var query = string.Empty;
        if (allowedQueryKeys is { Count: > 0 })
        {
            query = string.Join("&", request.Query
                .Where(p => allowedQueryKeys.Contains(p.Key))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value.ToString())}"));
        }

        return $"{representation}|{salt}|{host}|{method}|{path}|{query}";
    }

    /// <summary>
    ///     Normalise a request path: lowercase, single trailing slash stripped,
    ///     leading slash preserved, empty → "/".
    /// </summary>
    private static string NormalisePath(string? pathValue)
    {
        var p = (pathValue ?? "/").ToLowerInvariant();
        if (p.Length > 1 && p.EndsWith('/'))
            p = p.TrimEnd('/');
        return p;
    }
}
