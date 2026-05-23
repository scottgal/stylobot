using Microsoft.Extensions.Options;

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
    bool IsExempt(string normalizedPath);
}

/// <summary>
///     Config-backed exempt store -- reads
///     <see cref="HoneypotDetectionOptions.ExemptPaths"/> via
///     <see cref="IOptionsMonitor{TOptions}"/> so live config reloads
///     (see <c>/stylobot/admin/reload</c>) take effect without restart.
/// </summary>
public sealed class ConfigHoneypotExemptStore : IHoneypotExemptStore
{
    private readonly IOptionsMonitor<HoneypotDetectionOptions> _options;

    public ConfigHoneypotExemptStore(IOptionsMonitor<HoneypotDetectionOptions> options)
    {
        _options = options;
    }

    public IReadOnlyCollection<string> GetExemptPaths() => _options.CurrentValue.ExemptPaths;

    public bool IsExempt(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath)) return false;

        foreach (var pattern in _options.CurrentValue.ExemptPaths)
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
