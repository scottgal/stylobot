using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     Classifies a request path into one of three coarse risk tiers so the
///     threat-intel contributor can modulate per-class weights. Pattern list is
///     deliberately FOSS-generic — high-confidence "this surface is sensitive
///     regardless of stack" markers like <c>/.env</c>, <c>/.git</c>,
///     <c>/admin*</c>, <c>/login</c>, <c>/wp-login.php</c>, <c>/checkout</c>,
///     <c>/api/token*</c>. Commercial layers a per-endpoint operator-edited
///     override on top via the existing pinned-endpoint store.
///
///     <para>Static-asset detection is extension-based so an operator's image
///     CDN-fronted gallery doesn't accidentally land in "Normal" and pick up
///     spurious threat-intel contributions for benign cloud-IP visitors.</para>
/// </summary>
public static class EndpointRiskClassifier
{
    // High-confidence "this is a sensitive surface" markers. Match is on the path's
    // first segment (case-insensitive), or a known dotfile / config probe pattern.
    // Conservative on purpose: false positives here turn into stronger policy
    // actions, so we only include patterns where "credentials / config / payment /
    // VCS leak" is unambiguous.
    private static readonly Regex SensitivePattern = new(
        @"^/(?:" +
            @"admin(?:istrator)?(?:/|$)" +     // /admin, /administrator
            @"|login(?:/|$|\.[a-z]+$)" +       // /login, /login.php
            @"|wp-login\.php$" +
            @"|wp-admin(?:/|$)" +
            @"|signin(?:/|$)" +
            @"|sign-in(?:/|$)" +
            @"|signup(?:/|$)" +
            @"|register(?:/|$)" +
            @"|checkout(?:/|$)" +
            @"|cart/checkout(?:/|$)" +
            @"|payment(?:s)?(?:/|$)" +
            @"|billing(?:/|$)" +
            @"|api/(?:auth|token|tokens|login|admin|keys?)(?:/|$)" +
            @"|oauth(?:/|$)" +
            @"|sso(?:/|$)" +
            @"|\.env(?:\.|$)" +                // /.env, /.env.local
            @"|\.git(?:/|$)" +                 // /.git, /.git/config
            @"|config(?:\.php|\.json|\.yml|\.yaml)?$" +
            @"|phpmyadmin(?:/|$)" +
            @"|server-status$" +
            @"|server-info$" +
            @"|\.ssh(?:/|$)" +
            @"|\.aws(?:/|$)" +
            @"|credentials(?:/|$|\.|json$)" +
            @"|secrets?(?:/|$|\.|json$)" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Static-asset extension list. Hot-path matches on the path's trailing extension.
    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp", ".avif",
        ".css", ".js", ".mjs", ".map",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp4", ".webm", ".ogg", ".mp3", ".wav",
        ".pdf", ".zip", ".gz", ".tar",
        ".txt"   // robots.txt, security.txt, etc.
    };

    /// <summary>
    ///     Classify a request path. Empty / null defaults to <see cref="EndpointRisk.Normal"/>
    ///     (refuses to silently down-weight an unknown path).
    /// </summary>
    public static EndpointRisk Classify(string? path)
    {
        if (string.IsNullOrEmpty(path)) return EndpointRisk.Normal;

        // Normalise: drop query string, ensure leading slash
        var q = path.IndexOf('?');
        var p = q >= 0 ? path[..q] : path;
        if (p.Length == 0 || p[0] != '/') p = "/" + p;

        // Static first (cheapest + most common): trailing extension test.
        var dot = p.LastIndexOf('.');
        if (dot > 0 && dot > p.LastIndexOf('/'))
        {
            var ext = p[dot..];
            if (StaticExtensions.Contains(ext)) return EndpointRisk.Static;
        }

        // Sensitive: regex match anchored on path start.
        if (SensitivePattern.IsMatch(p)) return EndpointRisk.Sensitive;

        return EndpointRisk.Normal;
    }
}
