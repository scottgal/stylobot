using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Normalizes request paths into route templates for Markov chain analysis.
///     Replaces dynamic segments (IDs, GUIDs, slugs) with placeholders and
///     optionally buckets paths by type ({search}, {detail}, {api}, etc.).
/// </summary>
public static partial class PathNormalizer
{
    /// <summary>
    ///     Normalize a raw request path to a route template.
    ///     Examples:
    ///       /product/12345          → /product/{id}
    ///       /api/v2/users/abc-def   → /api/v{v}/users/{slug}
    ///       /assets/css/style.css   → {static}
    ///       /search?q=foo&page=2    → /search
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // Strip query string
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
            path = path[..queryIndex];

        // Strip fragment
        var fragmentIndex = path.IndexOf('#');
        if (fragmentIndex >= 0)
            path = path[..fragmentIndex];

        // Normalize trailing slash
        if (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];

        // Static assets → single bucket
        if (IsStaticAsset(path))
            return "{static}";

        // Replace GUIDs first (before numeric replacement eats the digits)
        path = GuidRegex().Replace(path, "{guid}");

        // Replace hex hashes (32+ chars)
        path = HexHashRegex().Replace(path, "{hash}");

        // Replace version patterns: v2, v3.1
        path = VersionRegex().Replace(path, "v{v}");

        // Replace purely numeric segments
        path = NumericSegmentRegex().Replace(path, "{id}");

        // Replace slug-like segments (lowercase-with-dashes that look like content slugs)
        // Only if the segment is long enough to be a slug (not a short keyword)
        path = SlugSegmentRegex().Replace(path, match =>
            match.Value.Length > 20 ? "/{slug}" : match.Value);

        // Replace base64-looking segments
        path = Base64SegmentRegex().Replace(path, "{token}");

        return path.ToLowerInvariant();
    }

    /// <summary>
    ///     Classify a normalized path into a high-level bucket.
    ///     Useful for coarse-grained analysis when route templates are too granular.
    /// </summary>
    public static string Classify(string normalizedPath)
    {
        if (normalizedPath == "{static}") return "static";

        var lower = normalizedPath.ToLowerInvariant();

        if (lower.StartsWith("/api/") || lower.StartsWith("/_")) return "api";
        if (lower.Contains("/search") || lower.Contains("/find") || lower.Contains("/query")) return "search";
        if (lower.Contains("/login") || lower.Contains("/auth") || lower.Contains("/signin") ||
            lower.Contains("/signup") || lower.Contains("/register") || lower.Contains("/password") ||
            lower.Contains("/token") || lower.Contains("/oauth")) return "auth";
        if (lower.Contains("/admin") || lower.Contains("/dashboard") || lower.Contains("/manage")) return "admin";
        if (lower.Contains("{id}") || lower.Contains("{guid}") || lower.Contains("{slug}")) return "detail";
        if (lower.EndsWith("/feed") || lower.EndsWith("/rss") || lower.EndsWith("/atom") ||
            lower.EndsWith("/sitemap") || lower.EndsWith("/robots.txt")) return "meta";
        if (lower == "/" || lower == "/index") return "home";

        return "page";
    }

    private static bool IsStaticAsset(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;

        return ext.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".woff", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".woff2", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".eot", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".avif", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".webm", StringComparison.OrdinalIgnoreCase);
    }

    // GUIDs: 8-4-4-4-12 hex pattern
    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRegex();

    // Hex hashes: 32+ consecutive hex chars (MD5, SHA1, SHA256)
    [GeneratedRegex(@"(?<=/)[0-9a-f]{32,}(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HexHashRegex();

    // Version patterns: /v2/, /v3.1/
    [GeneratedRegex(@"(?<=/)v(\d+(?:\.\d+)?)(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    // Purely numeric path segments: /123/
    [GeneratedRegex(@"(?<=/)\d+(?=/|$)")]
    private static partial Regex NumericSegmentRegex();

    // Slug-like segments: /my-long-blog-post-title/
    [GeneratedRegex(@"/[a-z0-9]+(?:-[a-z0-9]+){3,}(?=/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex SlugSegmentRegex();

    // Base64-like segments: long alphanumeric with + / = padding
    [GeneratedRegex(@"(?<=/)[A-Za-z0-9+/]{20,}={0,2}(?=/|$)")]
    private static partial Regex Base64SegmentRegex();
}
