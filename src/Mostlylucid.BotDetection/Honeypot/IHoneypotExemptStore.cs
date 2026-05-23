using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Honeypot;

/// <summary>
///     Source of operator-set exempt paths. The FOSS default
///     (<see cref="ConfigHoneypotExemptStore"/>) reads
///     <see cref="HoneypotDetectionOptions.ExemptPaths"/>. Commercial builds
///     can register a SQLite-backed mutable store so the dashboard
///     "Mark as legitimate" action persists without an appsettings.json
///     edit + restart.
/// </summary>
public interface IHoneypotExemptStore
{
    /// <summary>
    ///     Returns the current exempt-path list. Glob-aware: trailing
    ///     <c>*</c> matches a prefix (the tagger applies the same matcher
    ///     used by the catalog).
    /// </summary>
    IReadOnlyCollection<string> GetExemptPaths();

    /// <summary>
    ///     True if the given normalised path is operator-exempted from
    ///     Tier 2 signals. Tier 1 paths always return false (never
    ///     exempt-able).
    /// </summary>
    /// <param name="normalizedPath">Normalised request path.</param>
    /// <param name="context">
    ///     Optional <see cref="HttpContext"/> -- when provided, the resolver
    ///     also consults the active site profile's <c>framework_paths</c>
    ///     so per-host stack-aware exemptions take effect (e.g. a WordPress
    ///     host's <c>/wp-login.php</c> is legitimately served).
    /// </param>
    bool IsExempt(string normalizedPath, HttpContext? context = null);
}

/// <summary>
///     Config-backed exempt store -- reads
///     <see cref="HoneypotDetectionOptions.ExemptPaths"/> via
///     <see cref="IOptionsMonitor{TOptions}"/> so live config reloads
///     (see <c>/stylobot/admin/reload</c>) take effect without restart.
///     When a <see cref="HttpContext"/> is supplied to <see cref="IsExempt"/>,
///     the active site profile's <c>framework_paths</c> are layered on top
///     of the global exempt list (per-host stack-aware exemptions).
/// </summary>
public sealed class ConfigHoneypotExemptStore : IHoneypotExemptStore
{
    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;
    private readonly ISiteProfileResolver? _profileResolver;

    public ConfigHoneypotExemptStore(
        IOptionsMonitor<HoneypotDetectionOptions> options,
        ISiteProfileResolver? profileResolver = null)
    {
        _options = options;
        _profileResolver = profileResolver;
    }

    public IReadOnlyCollection<string> GetExemptPaths() => _options.CurrentValue.ExemptPaths;

    public bool IsExempt(string normalizedPath, HttpContext? context = null)
    {
        if (string.IsNullOrEmpty(normalizedPath)) return false;

        // 1. Operator-supplied global list (appsettings).
        if (MatchesAny(normalizedPath, _options.CurrentValue.ExemptPaths))
            return true;

        // 2. Per-host site profile framework_paths.
        if (context is not null && _profileResolver is not null)
        {
            var profile = _profileResolver.Resolve(context);
            if (profile?.Honeypot?.FrameworkPaths is { Count: > 0 } framework
                && MatchesAny(normalizedPath, framework))
                return true;
        }

        return false;
    }

    private static bool MatchesAny(string normalizedPath, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern)) continue;

            if (pattern.Length > 1 && pattern[^1] == '*')
            {
                var prefix = pattern.AsSpan(0, pattern.Length - 1);
                if (normalizedPath.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (normalizedPath.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            else if (normalizedPath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase)
                     && normalizedPath.Length > pattern.Length
                     && normalizedPath[pattern.Length] == '/')
            {
                return true;
            }
        }
        return false;
    }
}
