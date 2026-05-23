using System.Collections.Frozen;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Canonical honeypot path catalog used by both the pre-detection tagger
///     (<see cref="HoneypotPathTagger"/>) and the Wave 0 contributor
///     (<see cref="HoneypotLinkContributor"/>). Splits known honeypot paths
///     into two tiers based on false-positive risk, so the detection pipeline
///     can emit a "verified bad" signal for paths that have zero legitimate
///     use and a softer "probable" signal for paths that COULD be real on a
///     specific stack (WordPress sites really do host <c>/wp-login.php</c>;
///     Spring Boot apps really do expose <c>/actuator</c>).
/// </summary>
/// <remarks>
///     <para>
///         Tier 1 (<see cref="AlwaysHoneypot"/>) -- exposing the file is a
///         configuration mistake on every site that exists. A human visitor
///         will never type these into a browser. Hitting one is a verified-bad
///         signal at 0.95 confidence. Not exempt-able.
///     </para>
///     <para>
///         Tier 2 (<see cref="ProbableHoneypot"/>) -- usually scanner traffic,
///         but might be a real endpoint on a particular deployment. Emits a
///         softer 0.75 signal. Operators can add specific paths to
///         <see cref="HoneypotDetectionOptions.ExemptPaths"/> (or the
///         dashboard "Mark as legitimate" action) to suppress the signal for
///         their site.
///     </para>
///     <para>
///         Curated from OWASP CRS <c>restricted-files.data</c>, CrowdSec
///         <c>sensitive_data.txt</c> + <c>backdoors.txt</c>, SecLists
///         <c>quickhits.txt</c>, and ayoubfathi/leaky-paths. Anchored to the
///         path root (must start with <c>/</c>); a trailing <c>*</c> means
///         "match anything that starts with this prefix" including
///         dot-suffix variants like <c>.env.local.save</c>.
///     </para>
/// </remarks>
public static class HoneypotPathDefinitions
{
    // ──────────────────────────────────────────────────────────────────
    //  Tier 1 -- AlwaysHoneypot
    //  No legitimate site exposes these. Anyone requesting one is hostile.
    //  Not operator-exempt-able; the request always emits the strong signal.
    // ──────────────────────────────────────────────────────────────────
    public static readonly FrozenSet<string> AlwaysHoneypot =
        BuildSet(
            // ── Cloud + SSH credentials (highest signal -- never legitimate) ──
            "/.aws/credentials", "/.aws/config",
            "/.ssh/id_rsa", "/.ssh/id_dsa", "/.ssh/id_ed25519", "/.ssh/id_ecdsa",
            "/.ssh/authorized_keys", "/.ssh/known_hosts",
            "/.gcp/credentials.json", "/.azure/credentials",
            "/.kube/config", "/kubeconfig",
            "/.docker/config.json", "/.dockercfg",

            // ── Bare SSH keys at root ──
            "/id_rsa", "/id_dsa", "/id_ed25519", "/id_ecdsa",
            "/.pgpass", "/.my.cnf", "/.netrc",
            "/.npmrc", "/.yarnrc",

            // ── .env family (glob -- catches .env, .env.local, .env.local.save, .env.production.bak, etc) ──
            "/.env*",

            // ── Version control directories exposed ──
            "/.git/config", "/.git/HEAD", "/.git/index", "/.git/logs/HEAD",
            "/.git/refs/heads/master", "/.git/refs/heads/main",
            "/.svn/entries", "/.svn/wc.db",
            "/.hg/hgrc", "/.hg/store",
            "/.bzr/branch-format",

            // ── Database dumps + site archives ──
            "/backup.sql", "/backup.zip", "/backup.tar.gz", "/backup.rar",
            "/db.sql", "/database.sql", "/dump.sql", "/data.sql",
            "/site.sql", "/mysql.sql", "/db_backup.sql",
            "/site.zip", "/www.zip", "/html.zip", "/web.zip",
            "/site.tar.gz", "/archive.zip", "/export.zip",

            // ── Path traversal / SSRF probes (already exploiting) ──
            "/etc/passwd", "/etc/shadow", "/etc/hosts",
            "/proc/self/environ", "/proc/version",
            "/windows/win.ini", "/boot.ini",

            // ── Cloud metadata SSRF probes ──
            "/latest/meta-data", "/metadata/v1", "/computeMetadata/v1",

            // ── wp-config.php variants (the actual WordPress config, never web-served) ──
            "/wp-config.php.bak", "/wp-config.php.old", "/wp-config.php.save",
            "/wp-config.php.swp", "/wp-config.php.txt", "/wp-config.php~",
            "/wp-config.bak", "/wp-config.old", "/wp-config.txt",

            // ── Webshells (verified malicious) ──
            "/c99.php", "/r57.php", "/wso.php", "/wso2.php",
            "/b374k.php", "/alfa.php", "/backdoor.php",
            "/shell.php", "/cmd.php", "/eval.php",

            // ── Credential files at root ──
            "/credentials", "/credentials.json", "/credentials.xml",
            "/secrets.json", "/secrets.yml", "/secrets.yaml",
            "/jwt.json", "/token.json", "/tokens.json",
            "/.env.local.php"
        );

    // ──────────────────────────────────────────────────────────────────
    //  Tier 2 -- ProbableHoneypot
    //  Almost always scanner traffic, but COULD be a real endpoint on this
    //  specific site (WordPress login, Spring Boot Actuator, etc).
    //  Softer signal; per-path exemption supported.
    // ──────────────────────────────────────────────────────────────────
    public static readonly FrozenSet<string> ProbableHoneypot =
        BuildSet(
            // ── WordPress (real on WordPress sites) ──
            "/wp-login.php", "/wp-admin", "/xmlrpc.php",
            "/wp-content*", "/wp-includes*", "/wp-cron.php",
            "/wp-json/wp/v2/users", "/wp-config.php",

            // ── Database admin UIs (real if operator hosts them) ──
            "/phpmyadmin*", "/pma*", "/myadmin*", "/mysqladmin",
            "/adminer.php", "/adminer", "/dbadmin", "/sqlmanager",

            // ── Generic admin panels ──
            "/administrator*", "/cpanel*", "/webadmin",
            "/manager/html", "/console", "/jmx-console", "/web-console",

            // ── Spring Boot Actuator (real on SB apps with mgmt port leaked) ──
            "/actuator*", "/jolokia*",

            // ── API documentation (real if exposed intentionally) ──
            "/swagger.json", "/swagger-ui.html", "/swagger-ui*",
            "/api-docs", "/v1/api-docs", "/v2/api-docs", "/v3/api-docs",
            "/graphql/debug",

            // ── Dev/ops UIs (real if exposed) ──
            "/grafana*", "/jenkins*", "/kibana*", "/portainer*",
            "/solr/admin", "/solr/", "/elasticsearch*",
            "/_cat/indices", "/_cluster/health", "/_all/_search",

            // ── Other config + lock files ──
            "/composer.json", "/composer.lock",
            "/package.json", "/package-lock.json", "/yarn.lock",
            "/Gemfile", "/Gemfile.lock", "/Pipfile", "/Pipfile.lock",
            "/requirements.txt", "/Dockerfile", "/.dockerenv",
            "/docker-compose.yml", "/docker-compose.yaml",
            "/Makefile", "/Vagrantfile",
            "/Jenkinsfile", "/.gitlab-ci.yml", "/.travis.yml",
            "/.circleci/config.yml",

            // ── .NET / Java settings files ──
            "/web.config", "/web.config.bak", "/web.config.old", "/web.config.txt",
            "/appsettings.json", "/appsettings.Development.json",
            "/application.yml", "/application.properties",

            // ── Other application config ──
            "/config.php", "/configuration.php",
            "/config.yml", "/config.yaml", "/config.json", "/config.xml",
            "/config.inc", "/config.inc.php", "/config.bak",
            "/settings.php", "/settings.py", "/settings.json", "/settings.yml",
            "/.htaccess", "/.htpasswd", "/.htaccess.bak",

            // ── Debug + info disclosure ──
            "/debug.php", "/phpinfo.php", "/info.php", "/test.php", "/test.html",
            "/_profiler*", "/_debugbar*", "/__debug__*",
            "/elmah.axd", "/trace.axd",
            "/server-status", "/server-info",

            // ── CGI / legacy exploit surfaces ──
            "/cgi-bin*", "/fckeditor*", "/kcfinder*", "/elfinder*",

            // ── IDE / editor artefacts ──
            "/.idea*", "/.vscode*", "/.project", "/.classpath", "/.editorconfig",
            "/.DS_Store", "/Thumbs.db", "/desktop.ini",

            // ── Log files ──
            "/error.log", "/access.log", "/debug.log",
            "/app.log", "/application.log",
            "/logs/error.log", "/logs/access.log",

            // ── Backup directories ──
            "/backup", "/backups", "/bak", "/old", "/tmp",

            // ── CMS-specific probes ──
            "/sites/default/files", "/sites/default/settings.php",
            "/user/login", "/user/register",
            "/misc/drupal.js",
            "/downloader", "/app/etc/local.xml"
        );

    // ──────────────────────────────────────────────────────────────────
    //  Suspicious file extensions
    //  Any path ending in one of these is suspicious regardless of prefix.
    //  Used as a Tier 2 hint -- the contributor emits a softer signal so
    //  benign matches (e.g. legit /reports/q4.zip) don't get banned, but
    //  the typical bot scan for /backup.sql.bak still gets flagged.
    // ──────────────────────────────────────────────────────────────────
    public static readonly FrozenSet<string> SuspiciousExtensions =
        BuildSet(
            ".sql", ".bak", ".old", ".orig", ".save", ".swp", ".swo",
            ".dist", ".inc", ".conf", ".cfg", ".ini",
            ".tar", ".tar.gz", ".tgz", ".gz", ".zip", ".rar", ".7z",
            ".pem", ".key", ".crt", ".csr", ".p12", ".pfx",
            ".db", ".sqlite", ".sqlite3", ".mdb",
            ".log"
        );

    // ──────────────────────────────────────────────────────────────────
    //  Public match API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Classifies a normalised request path against the tier catalog.
    ///     Path must already be lower-cased + normalised (decoded, traversal
    ///     resolved, redundant slashes collapsed) -- the contributor does
    ///     this via <see cref="NormalizePath"/> before calling.
    /// </summary>
    /// <param name="path">Normalised request path beginning with <c>/</c>.</param>
    /// <param name="matchedPattern">The catalog pattern that matched, or null.</param>
    /// <returns>The tier the path matched, or <see cref="HoneypotTier.None"/>.</returns>
    public static HoneypotTier Classify(string path, out string? matchedPattern)
    {
        matchedPattern = null;
        if (string.IsNullOrEmpty(path)) return HoneypotTier.None;

        // Tier 1 first -- highest signal, can't be exempted, runs hottest.
        if (TryMatch(path, AlwaysHoneypot, out matchedPattern))
            return HoneypotTier.Always;

        if (TryMatch(path, ProbableHoneypot, out matchedPattern))
            return HoneypotTier.Probable;

        // Suspicious extension fallback (Tier 2 strength). Skipped for paths
        // already classified above; we only get here if nothing matched.
        foreach (var ext in SuspiciousExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                matchedPattern = "*" + ext;
                return HoneypotTier.Probable;
            }
        }

        return HoneypotTier.None;
    }

    /// <summary>
    ///     Pattern-matches a normalised path against a set of catalog
    ///     entries. Supports a trailing <c>*</c> glob (no other wildcards)
    ///     and falls back to exact + path-segment-prefix match otherwise.
    /// </summary>
    public static bool TryMatch(string path, FrozenSet<string> patterns, out string? matched)
    {
        // Exact match: O(1) in the frozen set.
        if (patterns.Contains(path))
        {
            matched = path;
            return true;
        }

        // Glob (suffix *) + segment-prefix scan.
        foreach (var pattern in patterns)
        {
            if (pattern.Length > 1 && pattern[^1] == '*')
            {
                // Glob: prefix-match without requiring a path-segment boundary
                // so /.env* catches .env.local.save (next char `.`, not `/`).
                var prefix = pattern.AsSpan(0, pattern.Length - 1);
                if (path.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    matched = pattern;
                    return true;
                }
            }
            else if (path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                     && (path.Length == pattern.Length || path[pattern.Length] == '/'))
            {
                // Non-glob: require path-segment boundary so /wp-admin matches
                // /wp-admin/post.php but not /wp-administrator-tools.
                matched = pattern;
                return true;
            }
        }

        matched = null;
        return false;
    }

    /// <summary>
    ///     Normalises a raw request path: decodes percent-encoding (twice,
    ///     catching <c>%252e</c>-style double-encoded probes), strips null
    ///     bytes, normalises backslashes to forward slashes, collapses
    ///     repeated slashes, resolves <c>.</c> + <c>..</c> segments, and
    ///     lower-cases the result. Used by both the tagger and the
    ///     contributor.
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        var decoded = Uri.UnescapeDataString(path);
        if (decoded != path)
            decoded = Uri.UnescapeDataString(decoded);

        if (decoded.Contains('\0'))
            decoded = decoded.Replace("\0", "");

        if (decoded.Contains('\\'))
            decoded = decoded.Replace('\\', '/');

        // Collapse repeated slashes (//foo -> /foo).
        if (decoded.Contains("//"))
        {
            var sb = new System.Text.StringBuilder(decoded.Length);
            char prev = '\0';
            foreach (var c in decoded)
            {
                if (c == '/' && prev == '/') continue;
                sb.Append(c);
                prev = c;
            }
            decoded = sb.ToString();
        }

        // Resolve . and .. segments.
        if (decoded.Contains("/.") || decoded.Contains("/.."))
        {
            var segments = decoded.Split('/');
            var stack = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment is "." or "") continue;
                if (segment == "..")
                {
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    continue;
                }
                stack.Add(segment);
            }
            decoded = "/" + string.Join('/', stack);
        }

        return decoded.ToLowerInvariant();
    }

    private static FrozenSet<string> BuildSet(params string[] entries) =>
        entries.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     Honeypot tier produced by <see cref="HoneypotPathDefinitions.Classify"/>.
/// </summary>
public enum HoneypotTier
{
    /// <summary>No match -- not a known honeypot path.</summary>
    None = 0,

    /// <summary>
    ///     Always-honeypot match. No legitimate site exposes this; emit the
    ///     strong VerifiedBot signal at 0.95 confidence. Not exempt-able.
    /// </summary>
    Always = 1,

    /// <summary>
    ///     Probable-honeypot match. Real on some stacks (WordPress, Spring
    ///     Boot etc); emit a softer Bot signal at 0.75 confidence. Operators
    ///     can suppress per-path via the exempt list.
    /// </summary>
    Probable = 2
}
