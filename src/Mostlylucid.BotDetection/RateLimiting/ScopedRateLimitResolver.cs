using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;

namespace Mostlylucid.BotDetection.RateLimiting;

/// <summary>
///     Walks the <see cref="RateLimitOptions"/> scope tree to produce the
///     ordered list of rules that apply to a given request, honouring the
///     per-scope <c>Inherit</c> flag and child-overrides-parent merge.
///     <para>
///     Walk order (outermost to innermost): global → domain → subdomain →
///     endpoint → method. Rules are merged child-overrides-parent on rule
///     name. A scope with <c>Inherit: false</c> truncates the chain: only
///     rules declared at-or-below that scope apply.
///     </para>
///     <para>
///     Detection is symmetric across FOSS and commercial: the resolver
///     walks the full tree in both editions. What's gated to commercial
///     is the MANAGEMENT surface (dashboard editor, hot-reload apply) --
///     not the resolution semantics themselves.
///     </para>
/// </summary>
public class ScopedRateLimitResolver
{
    private readonly IOptionsMonitor<RateLimitOptions> _options;
    private readonly DomainNormalizer? _domainNormalizer;

    public ScopedRateLimitResolver(
        IOptionsMonitor<RateLimitOptions> options,
        DomainNormalizer? domainNormalizer = null)
    {
        _options = options;
        _domainNormalizer = domainNormalizer;
    }

    /// <summary>
    ///     Returns the rules that apply to this request, in evaluation order
    ///     (most-specific first -- caller picks the first that fires).
    /// </summary>
    public virtual IReadOnlyList<RateLimitRule> ResolveRules(
        string? host,
        string path,
        string method)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return Array.Empty<RateLimitRule>();

        // Walk from outermost (global) to innermost (method); collect scopes
        // along the way. A scope with Inherit:false truncates: we DROP all
        // earlier-collected scopes and start from this one. The walk preserves
        // declaration order so child-overrides-parent works naturally.
        var scopes = new List<RateLimitScopeNode>();
        scopes.Add(new RateLimitScopeNode { Limits = options.Limits, Inherit = false });

        Walk(options, host, path, method, scopes);

        return Merge(scopes);
    }

    /// <summary>
    ///     Walk the full scope tree: global → domain → subdomain → endpoint →
    ///     method. Commercial can still override for extended concerns (e.g.
    ///     tenant-scoped dynamic reload) but the default walk works in both
    ///     editions.
    /// </summary>
    protected virtual void Walk(
        RateLimitOptions options,
        string? host,
        string path,
        string method,
        List<RateLimitScopeNode> scopes)
    {
        // Walk the domain layer first (if a Domains map is populated + the host matches).
        RateLimitScopeNode? domainScope = null;
        if (!string.IsNullOrEmpty(host) && options.Domains.Count > 0)
        {
            domainScope = MatchScope(options.Domains, host);
            // Also try the normalized eTLD+1 form if the raw host misses.
            if (domainScope is null)
            {
                var registrable = _domainNormalizer?.NormalizeDomain(host);
                if (!string.IsNullOrEmpty(registrable) && !string.Equals(registrable, host, StringComparison.OrdinalIgnoreCase))
                    domainScope = MatchScope(options.Domains, registrable);
            }
            if (domainScope is not null) scopes.Add(domainScope);
        }

        // Subdomain layer nested under the matched domain.
        RateLimitScopeNode? subdomainScope = null;
        if (domainScope is not null && !string.IsNullOrEmpty(host) && domainScope.Subdomains.Count > 0)
        {
            subdomainScope = MatchScope(domainScope.Subdomains, host);
            if (subdomainScope is not null) scopes.Add(subdomainScope);
        }

        // Endpoint layer: try the innermost active scope first (subdomain → domain → global).
        var endpointHost = subdomainScope?.Endpoints
                        ?? domainScope?.Endpoints
                        ?? options.Endpoints;
        var endpoint = MatchEndpoint(endpointHost, path);
        if (endpoint is null) return;
        scopes.Add(endpoint);

        // Method layer nested under endpoint.
        if (endpoint.Methods.TryGetValue(method, out var methodScope))
            scopes.Add(methodScope);
    }

    /// <summary>
    ///     First-match-wins glob over an endpoint dictionary. Keys ending in
    ///     <c>*</c> are prefix globs (<c>/api/*</c> matches <c>/api/users/42</c>);
    ///     otherwise exact-or-prefix-with-segment-boundary so
    ///     <c>/admin</c> matches <c>/admin/foo</c> but not <c>/administrator</c>.
    ///     Same semantics as the existing EndpointPolicy matcher so operators
    ///     see one consistent path syntax.
    /// </summary>
    protected static RateLimitScopeNode? MatchEndpoint(
        IReadOnlyDictionary<string, RateLimitScopeNode> endpoints,
        string path)
    {
        foreach (var kv in endpoints)
        {
            var pattern = kv.Key;
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            else if (path.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase))
            {
                return kv.Value;
            }
        }
        return null;
    }

    /// <summary>
    ///     First-match-wins over a scope dictionary. Exact match on the whole
    ///     key wins over any wildcard. Keys starting with <c>*.</c> are
    ///     wildcards (<c>*.example.com</c> matches <c>api.example.com</c> AND
    ///     <c>admin.example.com</c>).
    /// </summary>
    protected static RateLimitScopeNode? MatchScope(
        IReadOnlyDictionary<string, RateLimitScopeNode> scopes,
        string host)
    {
        if (scopes.TryGetValue(host, out var exact)) return exact;
        foreach (var kv in scopes)
        {
            if (!kv.Key.StartsWith("*.", StringComparison.Ordinal)) continue;
            var suffix = kv.Key.Substring(1); // ".example.com"
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        }
        return null;
    }

    /// <summary>
    ///     Collapse the collected scope chain into a single rule list.
    ///     Honours <see cref="RateLimitScopeNode.Inherit"/>: a scope with
    ///     <c>Inherit: false</c> drops every accumulated rule and starts
    ///     fresh from itself. The most-specific scope's version of a
    ///     same-named rule wins.
    /// </summary>
    private static IReadOnlyList<RateLimitRule> Merge(IReadOnlyList<RateLimitScopeNode> scopes)
    {
        var merged = new Dictionary<string, RateLimitRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in scopes)
        {
            if (!scope.Inherit && merged.Count > 0)
                merged.Clear();
            foreach (var kv in scope.Limits)
                merged[kv.Key] = kv.Value;          // child overrides parent
        }
        return merged.Values.ToList();
    }
}
