using Microsoft.Extensions.Options;

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
///     FOSS resolution walks global → endpoint → method. Domain / subdomain
///     scopes are commercial; this implementation accepts them in config
///     (so the YAML is forward-compatible) but skips them at walk time. The
///     commercial layer replaces <see cref="Walk"/> with the full traversal.
///     </para>
/// </summary>
public class ScopedRateLimitResolver
{
    private readonly IOptionsMonitor<RateLimitOptions> _options;

    public ScopedRateLimitResolver(IOptionsMonitor<RateLimitOptions> options)
    {
        _options = options;
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
    ///     FOSS walk: global → endpoint → method. Override in commercial to
    ///     also walk <see cref="RateLimitOptions.Domains"/> and the per-domain
    ///     <see cref="RateLimitScopeNode.Subdomains"/> trees before reaching
    ///     <see cref="RateLimitScopeNode.Endpoints"/>.
    /// </summary>
    protected virtual void Walk(
        RateLimitOptions options,
        string? host,
        string path,
        string method,
        List<RateLimitScopeNode> scopes)
    {
        var endpoint = MatchEndpoint(options.Endpoints, path);
        if (endpoint is null) return;
        scopes.Add(endpoint);

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
