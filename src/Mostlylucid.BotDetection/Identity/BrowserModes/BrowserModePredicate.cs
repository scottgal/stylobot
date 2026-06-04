using System.Collections.Frozen;
using System.Net.Http;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Compiled predicate for a single browser-mode YAML row. Each clause is a
///     conjunction; the row matches when every populated clause matches.
///     Compilation pre-freezes the YAML lists into <see cref="FrozenSet{T}"/>
///     and pre-parses path globs so per-request matching is allocation-free.
///
///     The vocabulary deliberately stays narrow — every clause maps to a raw
///     value the encoder already extracts (sec_fetch_pattern, method, accept,
///     accept_encoding, headers) or to a property on the live
///     <see cref="IRequestProbe"/> (path, header presence). Adding a clause type
///     here is a small code change, but adding a new mode is YAML-only.
/// </summary>
public sealed class BrowserModePredicate
{
    private readonly FrozenSet<string>? _methodIn;
    private readonly FrozenSet<int>? _secFetchPatternIn;
    private readonly FrozenSet<string>? _secFetchDestIn;
    private readonly bool? _upgradeInsecureRequests;
    private readonly string? _acceptContains;
    private readonly FrozenSet<string>? _acceptIn;
    private readonly IReadOnlyList<PathGlob>? _pathMatches;
    private readonly FrozenSet<string>? _hasHeader;
    private readonly IReadOnlyDictionary<string, string>? _headerEquals;

    private BrowserModePredicate(
        FrozenSet<string>? methodIn,
        FrozenSet<int>? secFetchPatternIn,
        FrozenSet<string>? secFetchDestIn,
        bool? upgradeInsecureRequests,
        string? acceptContains,
        FrozenSet<string>? acceptIn,
        IReadOnlyList<PathGlob>? pathMatches,
        FrozenSet<string>? hasHeader,
        IReadOnlyDictionary<string, string>? headerEquals)
    {
        _methodIn = methodIn;
        _secFetchPatternIn = secFetchPatternIn;
        _secFetchDestIn = secFetchDestIn;
        _upgradeInsecureRequests = upgradeInsecureRequests;
        _acceptContains = acceptContains;
        _acceptIn = acceptIn;
        _pathMatches = pathMatches;
        _hasHeader = hasHeader;
        _headerEquals = headerEquals;
    }

    /// <summary>
    ///     Returns true when the request shape (raw-values dict from
    ///     IdentityVectorContributor + the live probe over headers/path) matches
    ///     every populated clause. Clauses that the YAML left unset are
    ///     skipped; an entirely empty predicate matches every request (used by
    ///     the <c>unknown</c> fallback row).
    /// </summary>
    public bool Matches(IReadOnlyDictionary<string, object?> raw, IRequestProbe probe)
    {
        if (_methodIn is not null)
        {
            var method = AsString(raw, "session.method_pattern") ?? probe.Method;
            if (string.IsNullOrEmpty(method) || !_methodIn.Contains(method)) return false;
        }

        if (_secFetchPatternIn is not null)
        {
            if (raw.TryGetValue("hdr.sec_fetch_pattern", out var v) && v is int pattern)
            {
                if (!_secFetchPatternIn.Contains(pattern)) return false;
            }
            else
            {
                return false;
            }
        }

        if (_secFetchDestIn is not null)
        {
            var dest = probe.HeaderOrDefault("Sec-Fetch-Dest", string.Empty);
            if (string.IsNullOrEmpty(dest) || !_secFetchDestIn.Contains(dest)) return false;
        }

        if (_upgradeInsecureRequests is bool wantUir)
        {
            if (!raw.TryGetValue("hdr.upgrade_insecure_requests", out var v) || v is not bool actual || actual != wantUir)
                return false;
        }

        if (_acceptContains is not null)
        {
            var accept = AsString(raw, "hdr.accept") ?? string.Empty;
            if (!accept.Contains(_acceptContains, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (_acceptIn is not null)
        {
            var accept = AsString(raw, "hdr.accept") ?? string.Empty;
            if (!_acceptIn.Contains(accept)) return false;
        }

        if (_pathMatches is not null)
        {
            var path = probe.Path;
            var matched = false;
            for (var i = 0; i < _pathMatches.Count; i++)
            {
                if (_pathMatches[i].Matches(path)) { matched = true; break; }
            }
            if (!matched) return false;
        }

        if (_hasHeader is not null)
        {
            foreach (var name in _hasHeader)
                if (!probe.HasHeader(name)) return false;
        }

        if (_headerEquals is not null)
        {
            foreach (var (name, expected) in _headerEquals)
            {
                if (!string.Equals(probe.HeaderOrDefault(name, string.Empty), expected, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        return true;
    }

    private static string? AsString(IReadOnlyDictionary<string, object?> raw, string key)
        => raw.TryGetValue(key, out var v) ? v?.ToString() : null;

    public static BrowserModePredicate Compile(RequestCharacterPredicateYaml? yaml)
    {
        if (yaml is null)
            return new BrowserModePredicate(null, null, null, null, null, null, null, null, null);

        return new BrowserModePredicate(
            methodIn: yaml.MethodIn is { Count: > 0 } ? FrozenSet.ToFrozenSet(yaml.MethodIn, StringComparer.OrdinalIgnoreCase) : null,
            secFetchPatternIn: yaml.SecFetchPatternIn is { Count: > 0 } ? FrozenSet.ToFrozenSet(yaml.SecFetchPatternIn) : null,
            secFetchDestIn: yaml.SecFetchDestIn is { Count: > 0 } ? FrozenSet.ToFrozenSet(yaml.SecFetchDestIn, StringComparer.OrdinalIgnoreCase) : null,
            upgradeInsecureRequests: yaml.UpgradeInsecureRequests,
            acceptContains: yaml.AcceptContains,
            acceptIn: yaml.AcceptIn is { Count: > 0 } ? FrozenSet.ToFrozenSet(yaml.AcceptIn, StringComparer.OrdinalIgnoreCase) : null,
            pathMatches: yaml.PathMatches is { Count: > 0 } ? yaml.PathMatches.Select(PathGlob.Compile).ToArray() : null,
            hasHeader: yaml.HasHeader is { Count: > 0 } ? FrozenSet.ToFrozenSet(yaml.HasHeader, StringComparer.OrdinalIgnoreCase) : null,
            headerEquals: yaml.HeaderEquals is { Count: > 0 } ? new Dictionary<string, string>(yaml.HeaderEquals, StringComparer.OrdinalIgnoreCase) : null);
    }

    /// <summary>
    ///     Tiny glob matcher supporting one leading and/or trailing <c>*</c>.
    ///     The YAML inventory uses globs to declare path families
    ///     (e.g. <c>*/hub/negotiate*</c>) so the path list lives in YAML, not
    ///     a hand-rolled string switch in C#.
    /// </summary>
    public sealed class PathGlob
    {
        private readonly bool _leadingStar;
        private readonly bool _trailingStar;
        private readonly string _literal;

        private PathGlob(bool leading, bool trailing, string literal)
        {
            _leadingStar = leading;
            _trailingStar = trailing;
            _literal = literal;
        }

        public static PathGlob Compile(string pattern)
        {
            var leading = pattern.StartsWith('*');
            var trailing = pattern.EndsWith('*');
            var literal = pattern.Trim('*');
            return new PathGlob(leading, trailing, literal);
        }

        public bool Matches(string path)
        {
            if (_leadingStar && _trailingStar)
                return path.Contains(_literal, StringComparison.OrdinalIgnoreCase);
            if (_leadingStar)
                return path.EndsWith(_literal, StringComparison.OrdinalIgnoreCase);
            if (_trailingStar)
                return path.StartsWith(_literal, StringComparison.OrdinalIgnoreCase);
            return string.Equals(path, _literal, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>
///     Live request handle the predicate uses for clauses that need the
///     HttpContext rather than the encoded raw-values dict (path matches,
///     header presence, header equality). Implementations adapt to whatever
///     request shape the caller has; the contributor uses an HttpContext-backed
///     impl, unit tests use an inline fake.
/// </summary>
public interface IRequestProbe
{
    string Method { get; }
    string Path { get; }
    bool HasHeader(string name);
    string HeaderOrDefault(string name, string fallback);
}
