using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;

namespace Mostlylucid.BotDetection.SiteProfiles;

/// <summary>
///     Resolves the active <see cref="SiteProfile"/> for an incoming
///     request by matching the <c>Host</c> header against the configured
///     host map.
/// </summary>
public interface ISiteProfileResolver
{
    /// <summary>
    ///     Resolve the profile for the current request. Returns the
    ///     default profile when no host rule matches and the default
    ///     profile exists; null when neither matches.
    /// </summary>
    SiteProfile? Resolve(HttpContext context);

    /// <summary>Lookup by host string (lower-case, port-stripped). Same matcher as <see cref="Resolve"/>.</summary>
    SiteProfile? ResolveByHost(string host);
}

internal sealed class SiteProfileResolver : ISiteProfileResolver
{
    private const string CacheKey = "Site.Profile";

    private readonly ISiteProfileCatalog _catalog;
    private readonly ILogger<SiteProfileResolver> _logger;
    private readonly DomainNormalizer _domainNormalizer;
    private readonly FrozenDictionary<string, SiteProfile?> _exact;
    private readonly (string[] Segments, bool LeadingWildcard, SiteProfile? Profile)[] _wildcards;
    private readonly SiteProfile? _defaultProfile;

    public SiteProfileResolver(
        ISiteProfileCatalog catalog,
        IOptions<SiteMapOptions> map,
        DomainNormalizer domainNormalizer,
        ILogger<SiteProfileResolver> logger)
    {
        _catalog = catalog;
        _logger = logger;
        _domainNormalizer = domainNormalizer;

        var opts = map.Value;
        _defaultProfile = string.IsNullOrEmpty(opts.DefaultProfile)
            ? null
            : catalog.Get(opts.DefaultProfile);

        var exactBuilder = new Dictionary<string, SiteProfile?>(StringComparer.OrdinalIgnoreCase);
        var wildcards = new List<(string[] Segments, bool LeadingWildcard, SiteProfile? Profile)>();

        foreach (var rule in opts.Domains)
        {
            if (string.IsNullOrEmpty(rule.Host) || string.IsNullOrEmpty(rule.Profile)) continue;
            var profile = catalog.Get(rule.Profile);
            if (profile is null)
            {
                _logger.LogWarning("Site rule '{Host}' references unknown profile '{Profile}'", rule.Host, rule.Profile);
                continue;
            }

            var host = rule.Host.ToLowerInvariant();
            if (host.Contains('*'))
            {
                var segments = host.Split('.');
                var leading = segments.Length > 0 && segments[0] == "*";
                wildcards.Add((segments, leading, profile));
            }
            else
            {
                exactBuilder[host] = profile;
            }
        }

        _exact = exactBuilder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _wildcards = wildcards.ToArray();
    }

    public SiteProfile? Resolve(HttpContext context)
    {
        // Cache the resolved profile on the context so multi-read in a single
        // request is one lookup. Honeypot tagger + contributor + dashboard
        // chip all consult this; we don't want to re-match three times.
        if (context.Items.TryGetValue(CacheKey, out var cached))
            return cached as SiteProfile;

        var host = _domainNormalizer.NormalizeHost(context.Request.Host.Host);
        var profile = ResolveByHost(host);
        context.Items[CacheKey] = profile;
        return profile;
    }

    public SiteProfile? ResolveByHost(string host)
    {
        host = _domainNormalizer.NormalizeHost(host);
        if (string.IsNullOrEmpty(host)) return _defaultProfile;

        // Exact match wins.
        if (_exact.TryGetValue(host, out var exact) && exact is not null) return exact;

        // Wildcard scan in declaration order.
        var hostSegments = host.Split('.');
        foreach (var (pattern, leading, profile) in _wildcards)
        {
            if (MatchesWildcard(pattern, leading, hostSegments))
                return profile;
        }

        return _defaultProfile;
    }

    private static bool MatchesWildcard(string[] pattern, bool leading, string[] hostSegments)
    {
        if (leading)
        {
            // *.staging.example.com -- match when host has at least one extra
            // leading segment AND the suffix segments match exactly.
            var suffix = pattern.Length - 1;          // segments AFTER the *
            if (hostSegments.Length <= suffix) return false;
            var offset = hostSegments.Length - suffix;
            for (var i = 0; i < suffix; i++)
            {
                if (!string.Equals(pattern[i + 1], hostSegments[offset + i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        // Middle wildcard: api.*.example.com -- segment count must match.
        if (pattern.Length != hostSegments.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == "*") continue;
            if (!string.Equals(pattern[i], hostSegments[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

}
