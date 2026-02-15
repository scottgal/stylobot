using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.ApiHolodeck.Contributors;

/// <summary>
///     Contributor that detects when bots access hidden honeypot paths.
///     These are paths that real users would never visit (like /admin-secret, /.env, /wp-login.php).
///     Any access to these paths is a strong indicator of automated scanning.
/// </summary>
/// <remarks>
///     <para>
///         Honeypot paths can be:
///         - Paths that shouldn't exist on your site (/.env, /wp-admin for non-WordPress sites)
///         - Hidden links injected into pages that only crawlers would find
///         - Links in robots.txt disallow rules that bots ignore
///     </para>
///     <para>
///         This contributor provides VERY high confidence because humans don't accidentally
///         visit paths like "/.git/config" or "/phpmyadmin" on a site that doesn't have them.
///     </para>
/// </remarks>
public class HoneypotLinkContributor : ContributingDetectorBase
{
    // Common scanner paths — curated from OWASP CRS restricted-files.data, CrowdSec sensitive_data.txt,
    // SecLists quickhits.txt, and ayoubfathi/leaky-paths. Grouped by category for maintainability.
    private static readonly HashSet<string> DefaultHoneypotPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── WordPress probes ──
        "/wp-login.php", "/wp-admin", "/wp-config.php", "/xmlrpc.php", "/wp-includes",
        "/wp-content/uploads", "/wp-content/debug.log", "/wp-cron.php", "/wp-json",

        // ── Config / secrets files (OWASP CRS restricted-files.data) ──
        "/.env", "/.env.local", "/.env.production", "/.env.staging", "/.env.development",
        "/.env.backup", "/.env.old", "/.env.save",
        "/config.php", "/configuration.php", "/config.yml", "/config.yaml", "/config.json",
        "/config.xml", "/config.inc", "/config.inc.php", "/config.bak",
        "/settings.php", "/settings.py", "/settings.json", "/settings.yml",
        "/application.yml", "/application.properties", "/appsettings.json", "/appsettings.Development.json",
        "/web.config", "/web.config.bak", "/web.config.old", "/web.config.txt",
        "/.htaccess", "/.htpasswd", "/.htaccess.bak",
        "/composer.json", "/composer.lock", "/package.json", "/package-lock.json",
        "/Gemfile", "/Gemfile.lock", "/Pipfile", "/Pipfile.lock",
        "/requirements.txt", "/Dockerfile", "/docker-compose.yml", "/docker-compose.yaml",
        "/Makefile", "/Gruntfile.js", "/Gulpfile.js", "/Vagrantfile",
        "/wp-config.php.bak", "/wp-config.php.old", "/wp-config.php.save",

        // ── Version control exposure ──
        "/.git", "/.git/config", "/.git/HEAD", "/.git/index", "/.git/logs/HEAD",
        "/.git/refs/heads/master", "/.git/refs/heads/main", "/.gitignore",
        "/.svn", "/.svn/entries", "/.svn/wc.db",
        "/.hg", "/.hg/hgrc", "/.hg/store",
        "/.bzr", "/.cvs",

        // ── CI/CD & deployment secrets ──
        "/.github/workflows", "/.gitlab-ci.yml", "/.circleci/config.yml",
        "/Jenkinsfile", "/.travis.yml",
        "/.aws/credentials", "/.aws/config",
        "/.ssh/id_rsa", "/.ssh/id_ed25519", "/.ssh/authorized_keys", "/.ssh/known_hosts",
        "/.npmrc", "/.yarnrc", "/.dockercfg", "/.docker/config.json",
        "/.kube/config", "/kubeconfig",

        // ── Database admin panels ──
        "/phpmyadmin", "/pma", "/mysql", "/adminer.php", "/adminer",
        "/dbadmin", "/myadmin", "/mysqladmin", "/sqlmanager",

        // ── Database dumps & backups (CrowdSec sensitive_data.txt) ──
        "/backup.sql", "/backup.zip", "/backup.tar.gz", "/backup.rar",
        "/db.sql", "/database.sql", "/dump.sql", "/data.sql",
        "/site.sql", "/mysql.sql", "/db_backup.sql",
        "/backup", "/backups", "/bak", "/old", "/temp",
        "/site.zip", "/www.zip", "/html.zip", "/web.zip",
        "/site.tar.gz", "/archive.zip", "/export.zip",

        // ── Debug / info disclosure ──
        "/debug.php", "/phpinfo.php", "/info.php", "/test.php", "/test.html",
        "/server-status", "/server-info",
        "/_profiler", "/_debugbar", "/__debug__",
        "/debug", "/trace", "/metrics", "/health",
        "/actuator", "/actuator/env", "/actuator/health", "/actuator/info",
        "/actuator/configprops", "/actuator/mappings",
        "/swagger.json", "/swagger-ui.html", "/api-docs",
        "/v1/api-docs", "/v2/api-docs", "/v3/api-docs",
        "/.well-known/openid-configuration",
        "/elmah.axd", "/trace.axd",

        // ── Admin / management panels ──
        "/admin", "/admin.php", "/administrator", "/manager",
        "/admin/login", "/admin/config", "/cpanel", "/webadmin",
        "/login", "/panel", "/dashboard",

        // ── Shell / webshell / exploit (CrowdSec backdoors.txt) ──
        "/shell.php", "/c99.php", "/r57.php", "/webshell",
        "/cmd.php", "/eval.php", "/x.php", "/up.php",
        "/wso.php", "/b374k.php", "/alfa.php",
        "/backdoor.php", "/hack.php",

        // ── CGI / legacy exploits ──
        "/cgi-bin/", "/cgi-bin/test-cgi",
        "/fckeditor/", "/kcfinder/", "/elfinder/",
        "/ckeditor/", "/tinymce/",

        // ── API probes & debug endpoints ──
        "/.well-known/security.txt",
        "/api/debug", "/api/config", "/api/env", "/api/test",
        "/graphql/debug", "/graphql",
        "/_cat/indices", "/_all/_search", "/_cluster/health",
        "/console", "/jmx-console", "/web-console",
        "/solr/admin", "/solr/",

        // ── IDE / editor artefacts ──
        "/.idea", "/.vscode", "/.project", "/.classpath",
        "/.DS_Store", "/Thumbs.db", "/desktop.ini",
        "/.editorconfig",

        // ── Key / credential files (leaky-paths) ──
        "/id_rsa", "/id_dsa", "/id_ecdsa", "/id_ed25519",
        "/.pgpass", "/.my.cnf", "/.netrc",
        "/credentials", "/credentials.json", "/credentials.xml",
        "/secrets.json", "/secrets.yml", "/secrets.yaml",
        "/jwt.json", "/token.json",
        "/.env.local.php",

        // ── Log files ──
        "/error.log", "/access.log", "/debug.log",
        "/app.log", "/application.log",
        "/logs/error.log", "/logs/access.log",
        "/log", "/logs",

        // ── Common CMS probes (Joomla, Drupal, Magento) ──
        "/joomla", "/drupal", "/magento",
        "/sites/default/files", "/sites/default/settings.php",
        "/user/login", "/user/register",
        "/misc/drupal.js",
        "/downloader", "/app/etc/local.xml",

        // ── Path traversal / null-byte probes (treated as honeypot hits) ──
        "/etc/passwd", "/etc/shadow", "/etc/hosts",
        "/proc/self/environ", "/proc/version",
        "/windows/win.ini", "/boot.ini",

        // ── Cloud metadata SSRF probes ──
        "/latest/meta-data", "/metadata/v1",
        "/computeMetadata/v1"
    };

    private readonly ILogger<HoneypotLinkContributor> _logger;
    private readonly HolodeckOptions _options;

    public HoneypotLinkContributor(
        ILogger<HoneypotLinkContributor> logger,
        IOptions<HolodeckOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    public override string Name => "HoneypotLink";

    /// <inheritdoc />
    public override int Priority => 5; // Run early - this is a strong signal

    /// <inheritdoc />
    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(10); // Very fast path matching

    /// <inheritdoc />
    public override bool IsOptional => false;

    /// <inheritdoc />
    /// <remarks>Empty array = always run in the first wave</remarks>
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    /// <summary>
    ///     Normalizes a request path to prevent bypass via URL encoding or path traversal.
    ///     Decodes percent-encoded characters and resolves . and .. segments.
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Decode percent-encoded characters (e.g., %2e = '.', %65 = 'e')
        // Double-decode to catch double-encoding attacks (e.g., %252e)
        var decoded = Uri.UnescapeDataString(path);
        if (decoded != path)
            decoded = Uri.UnescapeDataString(decoded);

        // Remove null bytes (null byte injection)
        decoded = decoded.Replace("\0", "");

        // Normalize path separators (backslash → forward slash)
        decoded = decoded.Replace('\\', '/');

        // Collapse repeated slashes
        decoded = Regex.Replace(decoded, "/+", "/");

        // Resolve . and .. segments
        var segments = decoded.Split('/');
        var stack = new List<string>();
        foreach (var segment in segments)
        {
            if (segment == "." || segment == "")
                continue;
            if (segment == "..")
            {
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(segment);
        }

        return "/" + string.Join("/", stack);
    }

    /// <inheritdoc />
    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableHoneypotLinkDetection) return Task.FromResult(None());

        var rawPath = state.HttpContext?.Request.Path.Value ?? "";
        if (string.IsNullOrEmpty(rawPath)) return Task.FromResult(None());

        var path = NormalizePath(rawPath).ToLowerInvariant();

        // Combine default paths with configured paths
        var honeypotPaths = new HashSet<string>(DefaultHoneypotPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var configPath in _options.HoneypotPaths) honeypotPaths.Add(configPath.ToLowerInvariant());

        // Check for exact match
        if (honeypotPaths.Contains(path)) return Task.FromResult(CreateHoneypotHit(path, "exact"));

        // Check for prefix match (e.g., /wp-admin/anything)
        foreach (var honeypotPath in honeypotPaths)
            if (path.StartsWith(honeypotPath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(CreateHoneypotHit(path, "prefix"));

        // Check for suspicious file extensions in any path
        var suspiciousExtensions = new[]
        {
            ".sql", ".bak", ".old", ".env", ".config",
            ".log", ".swp", ".swo", ".save", ".orig",
            ".dist", ".inc", ".conf", ".cfg", ".ini",
            ".tar", ".tar.gz", ".tgz", ".gz", ".zip", ".rar", ".7z",
            ".pem", ".key", ".crt", ".csr", ".p12", ".pfx",
            ".db", ".sqlite", ".sqlite3", ".mdb"
        };
        foreach (var ext in suspiciousExtensions)
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(CreateSuspiciousExtensionHit(path, ext));

        // Check referrer - did they follow a hidden link from our honeypot page?
        var referer = state.HttpContext?.Request.Headers["Referer"].FirstOrDefault();
        if (!string.IsNullOrEmpty(referer) && IsHoneypotReferer(referer))
            return Task.FromResult(CreateHoneypotRefererHit(path, referer));

        return Task.FromResult(None());
    }

    private IReadOnlyList<DetectionContribution> CreateHoneypotHit(string path, string matchType)
    {
        _logger.LogWarning(
            "Honeypot path accessed: {Path} (match type: {MatchType})",
            path, matchType);

        var signals = ImmutableDictionary.CreateBuilder<string, object>();
        signals.Add("HoneypotPath", path);
        signals.Add("HoneypotMatchType", matchType);
        signals.Add("HoneypotTriggered", true);

        // This is a VERY strong signal - verified bad bot
        return
        [
            DetectionContribution.VerifiedBot(
                    Name,
                    $"Accessed honeypot path: {path} (match: {matchType})",
                    nameof(BotType.Scraper))
                with
                {
                    Category = "HoneypotTrap",
                    ConfidenceDelta = 0.95, // Very high confidence
                    Weight = 2.0, // Double weight
                    Signals = signals.ToImmutable()
                    // TriggerEarlyExit is already true from VerifiedBot
                }
        ];
    }

    private IReadOnlyList<DetectionContribution> CreateSuspiciousExtensionHit(string path, string extension)
    {
        _logger.LogWarning(
            "Suspicious file extension accessed: {Path} (extension: {Extension})",
            path, extension);

        var signals = ImmutableDictionary.CreateBuilder<string, object>();
        signals.Add("SuspiciousExtension", extension);
        signals.Add("SuspiciousPath", path);

        return
        [
            DetectionContribution.Bot(
                    Name,
                    "SuspiciousExtension",
                    0.7, // High but not verified
                    $"Accessed suspicious file extension: {extension}",
                    1.5, // weight
                    nameof(BotType.Scraper))
                with
                {
                    Signals = signals.ToImmutable()
                }
        ];
    }

    private IReadOnlyList<DetectionContribution> CreateHoneypotRefererHit(string path, string referer)
    {
        _logger.LogWarning(
            "Request from honeypot referer: {Path} (referer: {Referer})",
            path, referer);

        var signals = ImmutableDictionary.CreateBuilder<string, object>();
        signals.Add("HoneypotReferer", referer);
        signals.Add("RequestPath", path);
        signals.Add("FollowedHoneypotLink", true);

        return
        [
            DetectionContribution.VerifiedBot(
                    Name,
                    $"Followed hidden honeypot link from {referer}",
                    nameof(BotType.Scraper))
                with
                {
                    Category = "HoneypotLinkFollower",
                    ConfidenceDelta = 0.90,
                    Weight = 1.8,
                    Signals = signals.ToImmutable()
                    // TriggerEarlyExit is already true from VerifiedBot
                }
        ];
    }

    private bool IsHoneypotReferer(string referer)
    {
        // Check if the referer is one of our honeypot pages
        // This would be set up in HoneypotInjectionMiddleware
        try
        {
            var uri = new Uri(referer);
            var path = uri.AbsolutePath.ToLowerInvariant();

            // Check if they came from a page that has honeypot links
            // The middleware would set a marker indicating the page had injected links
            return path.Contains("/honeypot", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("trap", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}