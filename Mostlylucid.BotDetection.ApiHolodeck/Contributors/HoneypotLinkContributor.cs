using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

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
    // Common scanner paths (expanded list)
    private static readonly HashSet<string> DefaultHoneypotPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        // WordPress probes
        "/wp-login.php", "/wp-admin", "/wp-config.php", "/xmlrpc.php", "/wp-includes",
        // Config files
        "/.env", "/.env.local", "/.env.production", "/config.php", "/configuration.php",
        // Version control
        "/.git", "/.git/config", "/.git/HEAD", "/.svn", "/.hg",
        // Database admin
        "/phpmyadmin", "/pma", "/mysql", "/adminer.php", "/dbadmin",
        // Backups
        "/backup.sql", "/backup.zip", "/db.sql", "/database.sql", "/dump.sql",
        // Debug/dev
        "/debug.php", "/phpinfo.php", "/info.php", "/test.php",
        // Admin paths
        "/admin", "/admin.php", "/administrator", "/manager",
        // Shell/exploit
        "/shell.php", "/c99.php", "/r57.php", "/webshell",
        // API probes
        "/.well-known/security.txt", "/api/debug", "/graphql/debug",
        // Specific exploits
        "/cgi-bin/", "/fckeditor/", "/kcfinder/", "/elfinder/"
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

    /// <inheritdoc />
    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableHoneypotLinkDetection) return Task.FromResult(None());

        var path = state.HttpContext?.Request.Path.Value?.ToLowerInvariant() ?? "";

        if (string.IsNullOrEmpty(path)) return Task.FromResult(None());

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
        var suspiciousExtensions = new[] { ".sql", ".bak", ".old", ".env", ".config" };
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