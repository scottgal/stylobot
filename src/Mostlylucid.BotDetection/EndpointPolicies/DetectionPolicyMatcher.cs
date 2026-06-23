using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Stateless evaluation of a <see cref="DetectionPolicyRule"/> against
///     a request + its detection result. Returns the first matching rule's
///     index so the caller can dispatch its action. Numeric matchers
///     ("&gt;= 0.7") are parsed once per evaluation; for a hot path with
///     stable rule sets this is cheap relative to the alternative of
///     pre-compiling expressions.
/// </summary>
public static class DetectionPolicyMatcher
{
    /// <summary>
    ///     Returns the first matching rule (declaration order) or null.
    ///     Every non-null matcher on the rule must match; null matchers
    ///     wildcard. Empty-string Action rules are skipped -- a rule with
    ///     no action can't do anything useful and is almost certainly a
    ///     config error.
    /// </summary>
    public static DetectionPolicyRule? FindMatch(
        IReadOnlyList<DetectionPolicyRule> rules,
        HttpContext context,
        DetectionInputs inputs)
    {
        foreach (var rule in rules)
        {
            if (string.IsNullOrEmpty(rule.Action)) continue;
            if (Matches(rule, context, inputs))
                return rule;
        }
        return null;
    }

    private static bool Matches(DetectionPolicyRule rule, HttpContext ctx, DetectionInputs inputs)
    {
        // Request-shape matchers.
        if (!string.IsNullOrEmpty(rule.Host) && !MatchHost(rule.Host, ctx.Request.Host.Host))
            return false;
        if (!string.IsNullOrEmpty(rule.Method)
            && !string.Equals(rule.Method, "ANY", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(rule.Method, ctx.Request.Method, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrEmpty(rule.Path) && !MatchPath(rule.Path, ctx.Request.Path.Value ?? "/"))
            return false;

        // Detection-output matchers.
        if (!string.IsNullOrEmpty(rule.BotProbability)
            && !MatchNumeric(rule.BotProbability, inputs.BotProbability))
            return false;
        if (!string.IsNullOrEmpty(rule.Confidence)
            && !MatchNumeric(rule.Confidence, inputs.Confidence))
            return false;
        if (!MatchCategorical(rule.EffectiveTypes(), inputs.BotType)) return false;
        if (!MatchCategorical(rule.EffectiveThreats(), inputs.ThreatBand)) return false;

        return true;
    }

    /// <summary>
    ///     Host glob: exact, single-segment leading "*.", or "*" prefix mid-domain.
    ///     Same vocabulary as <see cref="EndpointPolicyRule.Host"/>.
    /// </summary>
    private static bool MatchHost(string pattern, string actual)
    {
        if (pattern.Equals(actual, StringComparison.OrdinalIgnoreCase)) return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return actual.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Path matcher with same semantics as the EndpointPolicy resolver.</summary>
    private static bool MatchPath(string pattern, string actual)
    {
        if (pattern.EndsWith('*'))
            return actual.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return actual.Equals(pattern, StringComparison.OrdinalIgnoreCase)
            || actual.StartsWith(pattern + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parse and apply a comparison expression like "&gt;= 0.7", "&lt; 0.5",
    ///     "== 1.0". Whitespace is optional. Returns false on parse failure
    ///     (operators see the rule as "didn't match" rather than a hard
    ///     load-time error -- config validation flags it separately).
    /// </summary>
    public static bool MatchNumeric(string expression, double actual)
    {
        var s = expression.AsSpan().Trim();
        if (s.IsEmpty) return true;

        // Order matters: check two-char operators before one-char.
        string? op = null;
        int opLen = 0;
        if (s.Length >= 2)
        {
            var head = s[..2];
            if (head is ">=" or "<=" or "==" or "!=") { op = head.ToString(); opLen = 2; }
        }
        if (op is null && s.Length >= 1)
        {
            var head = s[..1];
            if (head is ">" or "<") { op = head.ToString(); opLen = 1; }
        }
        if (op is null) return false;

        var rhs = s[opLen..].Trim();
        if (!double.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        return op switch
        {
            ">=" => actual >= value,
            "<=" => actual <= value,
            "==" => Math.Abs(actual - value) < 1e-9,
            "!=" => Math.Abs(actual - value) >= 1e-9,
            ">"  => actual > value,
            "<"  => actual < value,
            _ => false,
        };
    }

    /// <summary>
    ///     OR-list match: an empty list wildcards (every actual value matches),
    ///     a populated list requires <paramref name="actual"/> to equal one of
    ///     the entries case-insensitively. A null actual against a populated
    ///     list fails -- the rule wants a type and the request didn't have one.
    /// </summary>
    private static bool MatchCategorical(IReadOnlyList<string> values, string? actual)
    {
        if (values.Count == 0) return true;
        if (string.IsNullOrEmpty(actual)) return false;
        foreach (var v in values)
        {
            if (string.Equals(v, actual, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
///     The four detection-output values the matcher consumes. Defined as a
///     record so the test surface doesn't depend on the full
///     <c>AggregatedEvidence</c> wiring; production callers build one from
///     the evidence in DetectionPolicyMiddleware.
/// </summary>
public sealed record DetectionInputs(
    double BotProbability,
    double Confidence,
    string? BotType,
    string? ThreatBand);
