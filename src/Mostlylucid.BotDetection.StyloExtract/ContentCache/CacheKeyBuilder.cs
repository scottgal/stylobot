using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Mostlylucid.BotDetection.StyloExtract.ContentCache;

/// <summary>
///     Builds the content-cache key. Per the content-cache policy spec the key is
///     <c>host | method | normalised path | selected query values | representation | policy variant | version salt</c>.
/// </summary>
/// <remarks>
///     <para>
///         "Selected query values" means an explicit per-policy allow-list: only the named keys
///         participate in the key, so high-cardinality or auth-ish parameters (<c>utm_*</c>, session
///         tokens, ...) never fragment or poison the cache. An empty allow-list falls back to
///         including ALL query parameters — the back-compat-safe default (a wrong cross-query entry
///         is worse than a lower hit rate).
///     </para>
///     <para>
///         The path is normalised (trailing slash trimmed, root kept as <c>/</c>) but its case is
///         preserved — URL paths are case-sensitive on origin, so case-preserving avoids serving one
///         casing's content for another. The host is lower-cased and the method upper-cased.
///     </para>
/// </remarks>
public sealed class CacheKeyBuilder
{
    /// <summary>
    ///     Builds the cache key for <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The current request.</param>
    /// <param name="representation">HTML or Markdown — separates the two policy variants.</param>
    /// <param name="policyVariant">Registered policy name (e.g. <c>content-cache-search</c>).</param>
    /// <param name="versionSalt">Config salt; bumping it invalidates all entries without an explicit flush.</param>
    /// <param name="queryAllowList">Query keys selected to participate in the key. Empty = all keys.</param>
    public string Build(
        HttpRequest request,
        ContentRepresentation representation,
        string policyVariant,
        string versionSalt,
        IReadOnlyCollection<string> queryAllowList)
    {
        var host = request.Host.Host.ToLowerInvariant();
        var method = request.Method.ToUpperInvariant();
        var path = NormalisePath(request.Path.Value ?? "/");
        var query = SelectQuery(request.Query, queryAllowList);
        return $"cc|{versionSalt}|{policyVariant}|{representation}|{host}|{method}|{path}|{query}";
    }

    /// <summary>
    ///     Compatibility overload used by the legacy <c>content-cache</c> / <c>extract-markdown</c>
    ///     policy classes (each owns its own store, so the policy-variant segment is omitted there —
    ///     cross-variant collisions are impossible across separate stores).
    /// </summary>
    /// <param name="representation">Representation name (<c>"html"</c> or <c>"markdown"</c>).</param>
    public static string Build(
        HttpRequest request,
        string representation,
        string versionSalt,
        IReadOnlyCollection<string> queryAllowList)
    {
        var parsed = Enum.TryParse<ContentRepresentation>(representation, ignoreCase: true, out var value)
            ? value
            : ContentRepresentation.Html;
        return new CacheKeyBuilder().Build(request, parsed, string.Empty, versionSalt, queryAllowList);
    }

    /// <summary>Trims the trailing slash (keeping the root as <c>/</c>); preserves path case.</summary>
    internal static string NormalisePath(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static string SelectQuery(IQueryCollection query, IReadOnlyCollection<string> queryAllowList)
    {
        if (query.Count == 0) return string.Empty;

        var selected = queryAllowList.Count == 0
            ? query
            : query.Where(pair => queryAllowList.Contains(pair.Key, StringComparer.OrdinalIgnoreCase));

        return string.Join('&', selected
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.ToString())}"));
    }
}
