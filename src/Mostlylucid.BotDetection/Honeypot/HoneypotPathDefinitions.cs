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
///         Every catalog entry also carries a <see cref="HoneypotCategory"/>
///         tag describing the exploit class the scanner is going for
///         (Credentials / Config / VersionControl / Webshell etc). The tier
///         sets (<see cref="AlwaysHoneypot"/>, <see cref="ProbableHoneypot"/>)
///         are derived from the per-category catalog so the path data has
///         a single source of truth.
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
    // ─────────────────────────────────────────────────────────────────
    //  Per-(Tier, Category) catalog -- the single source of truth.
    //  AlwaysHoneypot + ProbableHoneypot below are derived from this.
    // ─────────────────────────────────────────────────────────────────

    private static readonly (HoneypotTier Tier, HoneypotCategory Category, string[] Paths)[] _catalog =
    {
        // ============================================================
        //  Tier 1 -- AlwaysHoneypot (zero legitimate use)
        // ============================================================

        (HoneypotTier.Always, HoneypotCategory.Credentials, new[]
        {
            // Cloud + service credentials
            "/.aws/credentials", "/.aws/config",
            "/.gcp/credentials.json", "/.azure/credentials",
            "/.kube/config", "/kubeconfig",
            "/.docker/config.json", "/.dockercfg",
            // SSH keys (under home dot-dir + bare-root variants attackers also try)
            "/.ssh/id_rsa", "/.ssh/id_dsa", "/.ssh/id_ed25519", "/.ssh/id_ecdsa",
            "/.ssh/authorized_keys", "/.ssh/known_hosts",
            "/id_rsa", "/id_dsa", "/id_ed25519", "/id_ecdsa",
            // Other secrets-bearing dotfiles
            "/.pgpass", "/.my.cnf", "/.netrc",
            "/.npmrc", "/.yarnrc",
            // Credential files at root
            "/credentials", "/credentials.json", "/credentials.xml",
            "/secrets.json", "/secrets.yml", "/secrets.yaml",
            "/jwt.json", "/token.json", "/tokens.json"
        }),

        (HoneypotTier.Always, HoneypotCategory.Config, new[]
        {
            // .env family. The *X* substring glob catches:
            //   /.env, /.env.local.save, /.env.production.bak
            //   /app/.env, /backend/.env, /laravel/.env, /admin/.env
            //   /config/.env.production.bak
            // The original /.env* prefix glob only caught root-level paths;
            // every mass-scanner now uses subdir variants (sig
            // 9z3avO7sKTd7NAYY896Yog hit /app/.env + /backend/.env).
            "*/.env*",
            // wp-config backup variants -- the actual config never web-served,
            // and attackers spray these under nested admin dirs too.
            "*/wp-config.php.bak", "*/wp-config.php.old", "*/wp-config.php.save",
            "*/wp-config.php.swp", "*/wp-config.php.txt", "*/wp-config.php~",
            "*/wp-config.bak", "*/wp-config.old", "*/wp-config.txt",
            "*/.env.local.php"
        }),

        (HoneypotTier.Always, HoneypotCategory.VersionControl, new[]
        {
            "/.git/config", "/.git/HEAD", "/.git/index", "/.git/logs/HEAD",
            "/.git/refs/heads/master", "/.git/refs/heads/main",
            "/.svn/entries", "/.svn/wc.db",
            "/.hg/hgrc", "/.hg/store",
            "/.bzr/branch-format"
        }),

        (HoneypotTier.Always, HoneypotCategory.Backup, new[]
        {
            // Database + site dumps
            "/backup.sql", "/backup.zip", "/backup.tar.gz", "/backup.rar",
            "/db.sql", "/database.sql", "/dump.sql", "/data.sql",
            "/site.sql", "/mysql.sql", "/db_backup.sql",
            "/site.zip", "/www.zip", "/html.zip", "/web.zip",
            "/site.tar.gz", "/archive.zip", "/export.zip"
        }),

        (HoneypotTier.Always, HoneypotCategory.PathTraversal, new[]
        {
            "/etc/passwd", "/etc/shadow", "/etc/hosts",
            "/proc/self/environ", "/proc/version",
            "/windows/win.ini", "/boot.ini"
        }),

        (HoneypotTier.Always, HoneypotCategory.Metadata, new[]
        {
            "/latest/meta-data", "/metadata/v1", "/computeMetadata/v1"
        }),

        (HoneypotTier.Always, HoneypotCategory.Webshell, new[]
        {
            "/c99.php", "/r57.php", "/wso.php", "/wso2.php",
            "/b374k.php", "/alfa.php", "/backdoor.php",
            "/shell.php", "/cmd.php", "/eval.php"
        }),

        // ============================================================
        //  Tier 2 -- ProbableHoneypot (legitimate on some stacks)
        // ============================================================

        (HoneypotTier.Probable, HoneypotCategory.Admin, new[]
        {
            // WordPress
            "/wp-login.php", "/wp-admin", "/xmlrpc.php",
            "/wp-content*", "/wp-includes*", "/wp-cron.php",
            "/wp-json/wp/v2/users", "/wp-config.php",
            // Generic admin panels
            "/administrator*", "/cpanel*", "/webadmin",
            "/manager/html", "/console", "/jmx-console", "/web-console",
            // Dev/ops UIs
            "/grafana*", "/jenkins*", "/kibana*", "/portainer*",
            "/solr/admin", "/solr/", "/elasticsearch*",
            "/_cat/indices", "/_cluster/health", "/_all/_search",
            // Spring Boot actuator
            "/actuator*", "/jolokia*"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Database, new[]
        {
            "/phpmyadmin*", "/pma*", "/myadmin*", "/mysqladmin",
            "/adminer.php", "/adminer", "/dbadmin", "/sqlmanager"
        }),

        (HoneypotTier.Probable, HoneypotCategory.ApiDoc, new[]
        {
            "/swagger.json", "/swagger-ui.html", "/swagger-ui*",
            "/api-docs", "/v1/api-docs", "/v2/api-docs", "/v3/api-docs",
            "/graphql/debug"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Config, new[]
        {
            // .NET / Java settings
            "/web.config", "/web.config.bak", "/web.config.old", "/web.config.txt",
            "/appsettings.json", "/appsettings.Development.json",
            "/application.yml", "/application.properties",
            // Generic application config
            "/config.php", "/configuration.php",
            "/config.yml", "/config.yaml", "/config.json", "/config.xml",
            "/config.inc", "/config.inc.php", "/config.bak",
            "/settings.php", "/settings.py", "/settings.json", "/settings.yml",
            "/.htaccess", "/.htpasswd", "/.htaccess.bak"
        }),

        (HoneypotTier.Probable, HoneypotCategory.BuildArtifact, new[]
        {
            // Lock files + manifests
            "/composer.json", "/composer.lock",
            "/package.json", "/package-lock.json", "/yarn.lock",
            "/Gemfile", "/Gemfile.lock", "/Pipfile", "/Pipfile.lock",
            "/requirements.txt", "/Dockerfile", "/.dockerenv",
            "/docker-compose.yml", "/docker-compose.yaml",
            "/Makefile", "/Vagrantfile",
            "/Jenkinsfile", "/.gitlab-ci.yml", "/.travis.yml",
            "/.circleci/config.yml",
            // IDE / editor artefacts
            "/.idea*", "/.vscode*", "/.project", "/.classpath", "/.editorconfig",
            "/.DS_Store", "/Thumbs.db", "/desktop.ini"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Debug, new[]
        {
            "/debug.php", "/phpinfo.php", "/info.php", "/test.php", "/test.html",
            "/_profiler*", "/_debugbar*", "/__debug__*",
            "/elmah.axd", "/trace.axd",
            "/server-status", "/server-info"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Backup, new[]
        {
            "/backup", "/backups", "/bak", "/old", "/tmp",
            // Log files leaked to web root
            "/error.log", "/access.log", "/debug.log",
            "/app.log", "/application.log",
            "/logs/error.log", "/logs/access.log"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Cgi, new[]
        {
            "/cgi-bin*", "/fckeditor*", "/kcfinder*", "/elfinder*"
        }),

        (HoneypotTier.Probable, HoneypotCategory.Cms, new[]
        {
            "/sites/default/files", "/sites/default/settings.php",
            "/user/login", "/user/register",
            "/misc/drupal.js",
            "/downloader", "/app/etc/local.xml"
        })
    };

    // ─────────────────────────────────────────────────────────────────
    //  Derived per-category + per-tier sets, frozen at class init.
    // ─────────────────────────────────────────────────────────────────

    private static readonly FrozenDictionary<HoneypotCategory, FrozenSet<string>> _byCategory =
        _catalog
            .GroupBy(t => t.Category)
            .ToFrozenDictionary(
                g => g.Key,
                g => g.SelectMany(t => t.Paths).ToFrozenSet(StringComparer.OrdinalIgnoreCase));

    private static readonly FrozenDictionary<string, HoneypotCategory> _patternToCategory =
        _catalog
            .SelectMany(t => t.Paths.Select(p => (Path: p, t.Category)))
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.First().Category, StringComparer.OrdinalIgnoreCase);

    /// <summary>Tier 1 -- every always-honeypot path, union across categories.</summary>
    public static readonly FrozenSet<string> AlwaysHoneypot =
        _catalog
            .Where(t => t.Tier == HoneypotTier.Always)
            .SelectMany(t => t.Paths)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tier 2 -- every probable-honeypot path, union across categories.</summary>
    public static readonly FrozenSet<string> ProbableHoneypot =
        _catalog
            .Where(t => t.Tier == HoneypotTier.Probable)
            .SelectMany(t => t.Paths)
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Suspicious file extensions. Each one maps to a category so
    ///     dashboards can render an intent-aware chip for these fallback
    ///     hits just like for catalog hits.
    /// </summary>
    private static readonly (string Ext, HoneypotCategory Category)[] _suspiciousExtensionCatalog =
    {
        // Database
        (".sql", HoneypotCategory.Database),
        (".db", HoneypotCategory.Database),
        (".sqlite", HoneypotCategory.Database),
        (".sqlite3", HoneypotCategory.Database),
        (".mdb", HoneypotCategory.Database),
        // Credentials (PKI material)
        (".pem", HoneypotCategory.Credentials),
        (".key", HoneypotCategory.Credentials),
        (".crt", HoneypotCategory.Credentials),
        (".csr", HoneypotCategory.Credentials),
        (".p12", HoneypotCategory.Credentials),
        (".pfx", HoneypotCategory.Credentials),
        // Backups + archives
        (".bak", HoneypotCategory.Backup),
        (".old", HoneypotCategory.Backup),
        (".orig", HoneypotCategory.Backup),
        (".save", HoneypotCategory.Backup),
        (".swp", HoneypotCategory.Backup),
        (".swo", HoneypotCategory.Backup),
        (".tar", HoneypotCategory.Backup),
        (".tar.gz", HoneypotCategory.Backup),
        (".tgz", HoneypotCategory.Backup),
        (".gz", HoneypotCategory.Backup),
        (".zip", HoneypotCategory.Backup),
        (".rar", HoneypotCategory.Backup),
        (".7z", HoneypotCategory.Backup),
        // Config / dev artefacts
        (".dist", HoneypotCategory.Config),
        (".inc", HoneypotCategory.Config),
        (".conf", HoneypotCategory.Config),
        (".cfg", HoneypotCategory.Config),
        (".ini", HoneypotCategory.Config),
        // Logs treated as Backup-class file dumps
        (".log", HoneypotCategory.Backup)
    };

    /// <summary>Back-compat: extension strings only, no category metadata.</summary>
    public static readonly FrozenSet<string> SuspiciousExtensions =
        _suspiciousExtensionCatalog.Select(x => x.Ext).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // ─────────────────────────────────────────────────────────────────
    //  Public match API
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Classifies a normalised request path against the tier catalog.
    ///     Back-compat single-out overload. Prefer <see cref="ClassifyDetailed"/>
    ///     when the category is wanted.
    /// </summary>
    public static HoneypotTier Classify(string path, out string? matchedPattern)
    {
        var result = ClassifyDetailed(path);
        matchedPattern = result.Pattern;
        return result.Tier;
    }

    /// <summary>
    ///     Classifies a normalised request path and returns Tier +
    ///     Category + matched pattern in one call.
    /// </summary>
    public static ClassificationResult ClassifyDetailed(string path)
    {
        if (string.IsNullOrEmpty(path))
            return new ClassificationResult(HoneypotTier.None, HoneypotCategory.None, null);

        if (TryMatch(path, AlwaysHoneypot, out var matched1))
            return new ClassificationResult(HoneypotTier.Always, LookupCategory(matched1!), matched1);

        if (TryMatch(path, ProbableHoneypot, out var matched2))
            return new ClassificationResult(HoneypotTier.Probable, LookupCategory(matched2!), matched2);

        foreach (var (ext, category) in _suspiciousExtensionCatalog)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return new ClassificationResult(HoneypotTier.Probable, category, "*" + ext);
        }

        return new ClassificationResult(HoneypotTier.None, HoneypotCategory.None, null);
    }

    /// <summary>
    ///     Look up the category for a catalog pattern.
    ///     <see cref="HoneypotCategory.None"/> when the pattern isn't a
    ///     catalog entry (e.g. a suspicious-extension match).
    /// </summary>
    public static HoneypotCategory CategoryForPattern(string pattern) =>
        _patternToCategory.TryGetValue(pattern, out var cat) ? cat : HoneypotCategory.None;

    /// <summary>Returns every catalog entry that belongs to <paramref name="category"/>.</summary>
    public static FrozenSet<string> GetPathsByCategory(HoneypotCategory category) =>
        _byCategory.TryGetValue(category, out var set)
            ? set
            : FrozenSet<string>.Empty;

    /// <summary>Returns the union of every catalog entry across all categories + tiers.</summary>
    public static IReadOnlyCollection<string> GetAllPaths() => _patternToCategory.Keys;

    /// <summary>
    ///     Pattern-matches a normalised path against a set of catalog
    ///     entries. Supported shapes:
    ///     <list type="bullet">
    ///         <item><c>X</c> -- exact match OR path-segment prefix
    ///               (<c>/wp-admin</c> matches <c>/wp-admin</c> and <c>/wp-admin/...</c>)</item>
    ///         <item><c>X*</c> -- prefix glob (<c>/.git/*</c>)</item>
    ///         <item><c>*X</c> -- suffix match (<c>*/.env</c> matches
    ///               <c>/.env</c>, <c>/app/.env</c>, <c>/backend/.env</c>)</item>
    ///         <item><c>*X*</c> -- substring match (<c>*/.env*</c> also
    ///               catches <c>/.env.local</c>, <c>/app/.env.production.bak</c>)</item>
    ///     </list>
    ///     The leading-glob shapes were added to close the catalogue gap
    ///     surfaced by the prod env-scanner sig <c>9z3avO7sKTd7NAYY896Yog</c>
    ///     which hit <c>/app/.env</c> and <c>/backend/.env</c> -- the
    ///     existing <c>/.env*</c> prefix-glob never matched subdir variants.
    /// </summary>
    public static bool TryMatch(string path, FrozenSet<string> patterns, out string? matched)
    {
        if (patterns.Contains(path))
        {
            matched = path;
            return true;
        }

        foreach (var pattern in patterns)
        {
            if (MatchPattern(path, pattern))
            {
                matched = pattern;
                return true;
            }
        }

        matched = null;
        return false;
    }

    internal static bool MatchPattern(string path, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;

        var len = pattern.Length;
        var leadingStar = len > 1 && pattern[0] == '*';
        var trailingStar = len > 1 && pattern[^1] == '*';

        if (leadingStar && trailingStar)
        {
            // *X* -- substring match. Two-char "**" is allowed but pointless;
            // an empty inner-span is treated as a match-anything wildcard
            // (operators sometimes use "**" as "tag this path" sentinel).
            var inner = pattern.AsSpan(1, len - 2);
            if (inner.IsEmpty) return true;
            return path.AsSpan().IndexOf(inner, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        if (leadingStar)
        {
            // *X -- suffix match.
            var suffix = pattern.AsSpan(1);
            return path.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        if (trailingStar)
        {
            // X* -- prefix glob (original behaviour).
            var prefix = pattern.AsSpan(0, len - 1);
            return path.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Exact match was handled by the FrozenSet hit above, so this branch
        // covers path-segment prefix: pattern "/wp-admin" matches "/wp-admin"
        // (handled above) and "/wp-admin/users".
        return path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
               && (path.Length == pattern.Length || path[pattern.Length] == '/');
    }

    private static HoneypotCategory LookupCategory(string pattern) =>
        _patternToCategory.TryGetValue(pattern, out var cat) ? cat : HoneypotCategory.None;

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
}

/// <summary>Result of <see cref="HoneypotPathDefinitions.ClassifyDetailed"/>.</summary>
public sealed record ClassificationResult(
    HoneypotTier Tier,
    HoneypotCategory Category,
    string? Pattern);

/// <summary>Honeypot tier produced by <see cref="HoneypotPathDefinitions.Classify"/>.</summary>
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
