using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity.BrowserModes;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>Matches the current request against the configured rules in order.</summary>
public interface IEndpointPolicyResolver
{
    /// <summary>
    ///     Returns the first matching rule, or null when no rule applies.
    ///     Hot-path: compiled into a frozen ordered list, no allocations
    ///     on the no-match case.
    /// </summary>
    EndpointPolicyMatch? Match(HttpContext context);

    /// <summary>All compiled rules. Used by the dashboard read surface.</summary>
    IReadOnlyList<EndpointPolicyRule> Rules { get; }
}

/// <summary>Result of a successful match.</summary>
public sealed record EndpointPolicyMatch(
    EndpointPolicyRule Rule,
    string ActionPolicyName,
    int? StatusCode,
    string? Reason);

internal sealed class ConfigEndpointPolicyResolver : IEndpointPolicyResolver
{
    private readonly IOptionsMonitor<EndpointPolicyOptions> _options;
    private readonly ILogger<ConfigEndpointPolicyResolver> _logger;
    private readonly IBrowserModeResolver? _modes;

    // Compiled matchers in declaration order. Recomputed when options
    // change (cheap; rule list is small).
    private CompiledRule[] _compiled = Array.Empty<CompiledRule>();
    private bool _enabled;

    public ConfigEndpointPolicyResolver(
        IOptionsMonitor<EndpointPolicyOptions> options,
        ILogger<ConfigEndpointPolicyResolver> logger,
        IBrowserModeResolver? modes = null)
    {
        _options = options;
        _logger = logger;
        // Optional so existing test rigs that don't register the BrowserMode
        // resolver keep working — rules without mode_in: skip the lookup,
        // rules with mode_in: but no resolver fail closed (no match) and
        // log on compile so the misconfiguration is loud at startup.
        _modes = modes;
        Recompile(options.CurrentValue);
        options.OnChange(Recompile);
    }

    public IReadOnlyList<EndpointPolicyRule> Rules => _options.CurrentValue.Rules;

    public EndpointPolicyMatch? Match(HttpContext context)
    {
        if (!_enabled || _compiled.Length == 0) return null;

        var request = context.Request;
        var host = NormaliseHost(request.Host.Host);
        var method = request.Method ?? "";
        var path = request.Path.Value ?? "";

        // Lazily computed -- only evaluated when at least one rule needs them.
        string? transport = null;
        string? protocolVersion = null;
        string? browserMode = null;

        foreach (var compiled in _compiled)
        {
            if (compiled.HostMatcher is { } hm && !hm.Matches(host)) continue;
            if (compiled.Method is { Length: > 0 } m
                && !string.Equals(m, method, StringComparison.OrdinalIgnoreCase))
                continue;
            if (compiled.PathMatcher is { } pm && !pm.Matches(path)) continue;
            if (compiled.Transport is { Length: > 0 } t)
            {
                transport ??= TransportClassifier.Classify(request);
                if (!string.Equals(t, transport, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (compiled.ProtocolVersion is { Length: > 0 } pv)
            {
                protocolVersion ??= TransportClassifier.ClassifyProtocolVersion(request);
                if (!string.Equals(pv, protocolVersion, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            if (compiled.ModeIn is { } modes)
            {
                // Browser-mode allowlist (composite-spec step 5). The classifier
                // is lazy + request-cached so the same call from the late
                // BrowserModeClassifierContributor won't repeat the work. When
                // the resolver isn't registered (config drift), the rule fails
                // closed: no mode → not in any allowlist → skip the rule.
                if (_modes is null) continue;
                browserMode ??= _modes.Resolve(context);
                if (!modes.Contains(browserMode)) continue;
            }

            return new EndpointPolicyMatch(
                compiled.Source,
                compiled.Source.Action,
                compiled.Source.StatusCode,
                compiled.Source.Reason);
        }

        return null;
    }

    private void Recompile(EndpointPolicyOptions options, string? _ = null)
    {
        _enabled = options.Enabled;
        if (!_enabled || options.Rules is null || options.Rules.Count == 0)
        {
            _compiled = Array.Empty<CompiledRule>();
            return;
        }

        var list = new List<CompiledRule>(options.Rules.Count);
        foreach (var rule in options.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Action))
            {
                _logger.LogWarning("EndpointPolicy rule with empty Action skipped (host={Host}, path={Path})", rule.Host, rule.Path);
                continue;
            }

            var modeIn = rule.ModeIn is { Count: > 0 }
                ? FrozenSet.ToFrozenSet(rule.ModeIn, StringComparer.OrdinalIgnoreCase)
                : null;
            if (modeIn is not null && _modes is null)
            {
                // Loud once at compile time so the operator sees the gap during
                // a Recompile -- mode_in: in YAML is meaningless without the
                // BrowserMode resolver in DI.
                _logger.LogWarning(
                    "EndpointPolicy rule (host={Host}, path={Path}) declares mode_in but IBrowserModeResolver is not registered — rule will never match",
                    rule.Host, rule.Path);
            }

            list.Add(new CompiledRule(
                rule,
                CompileHost(rule.Host),
                NormaliseMethod(rule.Method),
                CompilePath(rule.Path),
                NormaliseAny(rule.Transport),
                NormaliseAny(rule.ProtocolVersion),
                modeIn));
        }
        _compiled = list.ToArray();

        _logger.LogInformation("EndpointPolicy resolver compiled {Count} rule(s)", _compiled.Length);
    }

    private static string? NormaliseMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Equals("ANY", StringComparison.OrdinalIgnoreCase) ? null : value.ToUpperInvariant();
    }

    private static string? NormaliseAny(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Equals("ANY", StringComparison.OrdinalIgnoreCase) ? null : value.ToLowerInvariant();
    }

    private static HostMatcher? CompileHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "*") return null;
        var lower = host.ToLowerInvariant();
        if (!lower.Contains('*')) return new HostMatcher(lower, null, false);
        var segments = lower.Split('.');
        var leadingWildcard = segments.Length > 0 && segments[0] == "*";
        return new HostMatcher(null, segments, leadingWildcard);
    }

    private static PathMatcher? CompilePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var lower = path.ToLowerInvariant();
        var isGlob = lower.Length > 1 && lower[^1] == '*';
        return new PathMatcher(lower, isGlob);
    }

    private static string NormaliseHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return "";
        var colon = host.IndexOf(':');
        return (colon >= 0 ? host[..colon] : host).ToLowerInvariant().TrimEnd('.');
    }

    private sealed record CompiledRule(
        EndpointPolicyRule Source,
        HostMatcher? HostMatcher,
        string? Method,
        PathMatcher? PathMatcher,
        string? Transport,
        string? ProtocolVersion,
        FrozenSet<string>? ModeIn);

    private sealed class HostMatcher
    {
        private readonly string? _exact;
        private readonly string[]? _segments;
        private readonly bool _leadingWildcard;

        public HostMatcher(string? exact, string[]? segments, bool leadingWildcard)
        {
            _exact = exact;
            _segments = segments;
            _leadingWildcard = leadingWildcard;
        }

        public bool Matches(string host)
        {
            if (_exact is not null)
                return string.Equals(host, _exact, StringComparison.OrdinalIgnoreCase);

            if (_segments is null) return false;
            var hostSegments = host.Split('.');

            if (_leadingWildcard)
            {
                var suffix = _segments.Length - 1;
                if (hostSegments.Length <= suffix) return false;
                var offset = hostSegments.Length - suffix;
                for (var i = 0; i < suffix; i++)
                    if (!string.Equals(_segments[i + 1], hostSegments[offset + i], StringComparison.OrdinalIgnoreCase))
                        return false;
                return true;
            }

            if (_segments.Length != hostSegments.Length) return false;
            for (var i = 0; i < _segments.Length; i++)
            {
                if (_segments[i] == "*") continue;
                if (!string.Equals(_segments[i], hostSegments[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
    }

    private sealed class PathMatcher
    {
        private readonly string _pattern;
        private readonly bool _isGlob;

        public PathMatcher(string pattern, bool isGlob)
        {
            _pattern = pattern;
            _isGlob = isGlob;
        }

        public bool Matches(string path)
        {
            var lower = path.ToLowerInvariant();
            if (_isGlob)
            {
                var prefix = _pattern.AsSpan(0, _pattern.Length - 1);
                return lower.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }
            if (lower.Equals(_pattern, StringComparison.OrdinalIgnoreCase)) return true;
            return lower.StartsWith(_pattern, StringComparison.OrdinalIgnoreCase)
                   && lower.Length > _pattern.Length
                   && lower[_pattern.Length] == '/';
        }
    }
}
