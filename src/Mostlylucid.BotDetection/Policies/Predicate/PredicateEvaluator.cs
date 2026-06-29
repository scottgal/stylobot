using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Runtime semantics for the <see cref="Predicate"/> AST. Given a flat
///     dictionary of request signals (facet name -> value) the evaluator
///     decides whether a predicate is satisfied.
///
///     <para>
///     Unknown facets ALWAYS return <c>false</c> for the offending term --
///     they never bubble exceptions. This is the "silent miss" semantics the
///     resolver depends on: a request that simply doesn't carry a signal
///     should skip rules that depend on that signal, not blow up the policy
///     pipeline.
///     </para>
///
///     <para>
///     Numeric comparisons coerce both sides to <see cref="decimal"/> when
///     either side parses as a number, mirroring the AST's choice to round-
///     trip JSON numbers as <see cref="decimal"/>.
///     </para>
/// </summary>
public static class PredicateEvaluator
{
    /// <summary>
    ///     Evaluate <paramref name="predicate"/> against the request signals
    ///     without a sustain context. Callers that already have a rule id and
    ///     <see cref="SustainEvaluator"/> should prefer the stateful overload
    ///     that takes those arguments -- this stateless path returns
    ///     <c>false</c> for any <see cref="Predicate.Sustain"/> node it
    ///     encounters because it cannot know whether the inner has been true
    ///     long enough.
    /// </summary>
    public static bool Evaluate(Predicate predicate, IReadOnlyDictionary<string, object?> signals)
        => EvaluateStateless(predicate, signals);

    /// <summary>
    ///     Stateless evaluation -- treats <see cref="Predicate.Sustain"/>
    ///     nodes as <c>false</c>. Used by the predicate-trace explainer, the
    ///     backtest runner, and any caller without per-(rule, time) sustain
    ///     state. Composite predicates that mix Sustain with per-request
    ///     terms still evaluate the per-request terms exactly; the Sustain
    ///     subtree contributes <c>false</c> to its parent And/Or which is the
    ///     conservative degrade-safely answer the spec requires.
    /// </summary>
    public static bool EvaluateStateless(Predicate predicate, IReadOnlyDictionary<string, object?> signals) =>
        predicate switch
        {
            Predicate.And and => EvaluateAnd(and, signals, ruleId: Guid.Empty, sustain: null),
            Predicate.Or or => EvaluateOr(or, signals, ruleId: Guid.Empty, sustain: null),
            Predicate.Sustain => false,
            Predicate.Term term => EvaluateTerm(term, signals),
            _ => false
        };

    /// <summary>
    ///     Stateful evaluation -- <see cref="Predicate.Sustain"/> nodes
    ///     dispatch through <paramref name="sustain"/> with the provided
    ///     <paramref name="ruleId"/> so atoms are partitioned per rule. When
    ///     <paramref name="sustain"/> is <c>null</c> the evaluator falls back
    ///     to <see cref="EvaluateStateless"/> semantics so a host without
    ///     trigger-system DI keeps booting.
    /// </summary>
    public static bool Evaluate(
        Predicate predicate,
        IReadOnlyDictionary<string, object?> signals,
        Guid ruleId,
        SustainEvaluator? sustain) =>
        predicate switch
        {
            Predicate.And and => EvaluateAnd(and, signals, ruleId, sustain),
            Predicate.Or or => EvaluateOr(or, signals, ruleId, sustain),
            Predicate.Sustain s => sustain?.IsSatisfied(ruleId, s, signals) ?? false,
            Predicate.Term term => EvaluateTerm(term, signals),
            _ => false
        };

    private static bool EvaluateAnd(
        Predicate.And and,
        IReadOnlyDictionary<string, object?> signals,
        Guid ruleId,
        SustainEvaluator? sustain)
    {
        // Vacuous truth on an empty And matches the AST's identity element.
        foreach (var child in and.Children)
            if (!Evaluate(child, signals, ruleId, sustain))
                return false;
        return true;
    }

    private static bool EvaluateOr(
        Predicate.Or or,
        IReadOnlyDictionary<string, object?> signals,
        Guid ruleId,
        SustainEvaluator? sustain)
    {
        foreach (var child in or.Children)
            if (Evaluate(child, signals, ruleId, sustain))
                return true;
        return false;
    }

    private static bool EvaluateTerm(Predicate.Term term, IReadOnlyDictionary<string, object?> signals)
    {
        if (!signals.TryGetValue(term.Facet, out var facetValue))
            return false; // silent miss -- unknown facet

        return term.Op switch
        {
            PredicateOp.Eq => CompareScalar(facetValue, term.Value) == 0,
            PredicateOp.Neq => CompareScalar(facetValue, term.Value) != 0,
            PredicateOp.Gte => CompareScalar(facetValue, term.Value) is var c && c != int.MinValue && c >= 0,
            PredicateOp.Gt => CompareScalar(facetValue, term.Value) is var c2 && c2 != int.MinValue && c2 > 0,
            PredicateOp.Lte => CompareScalar(facetValue, term.Value) is var c3 && c3 != int.MinValue && c3 <= 0,
            PredicateOp.Lt => CompareScalar(facetValue, term.Value) is var c4 && c4 != int.MinValue && c4 < 0,
            PredicateOp.In => InList(facetValue, term.Value),
            PredicateOp.NotIn => !InList(facetValue, term.Value),
            PredicateOp.AnyIn => AnyIn(facetValue, term.Value),
            PredicateOp.AllIn => AllIn(facetValue, term.Value),
            PredicateOp.Between => Between(facetValue, term.Value),
            PredicateOp.Matches => Matches(facetValue, term.Value),
            PredicateOp.Contains => Contains(facetValue, term.Value),
            PredicateOp.InCidr => InCidr(facetValue, term.Value),
            _ => false
        };
    }

    // -------- CIDR --------

    /// <summary>
    ///     <c>facet in_cidr "10.0.0.0/8"</c> (single) or
    ///     <c>facet in_cidr ["10.0.0.0/8", "192.168.0.0/16"]</c> (any-match).
    ///     The facet must be a string IP -- IPv4 or IPv6. Malformed IP,
    ///     malformed CIDR, missing signal, or non-string CIDR value all
    ///     resolve to <c>false</c> in keeping with the evaluator's silent-miss
    ///     convention. Delegates to <see cref="CidrHelper.IsInSubnet(string, string)"/>
    ///     which owns the parse + mask arithmetic.
    /// </summary>
    private static bool InCidr(object? facet, object value)
    {
        if (facet is not string ipStr || string.IsNullOrWhiteSpace(ipStr))
            return false;

        switch (value)
        {
            case string single:
                return CidrHelper.IsInSubnet(ipStr, single);
            case string[] many:
                foreach (var cidr in many)
                    if (CidrHelper.IsInSubnet(ipStr, cidr))
                        return true;
                return false;
            default:
                return false;
        }
    }

    // -------- Scalar comparison --------

    /// <summary>
    ///     Three-way compare of <paramref name="facet"/> against
    ///     <paramref name="value"/>. Returns <c>int.MinValue</c> as the
    ///     "incomparable" sentinel; the ordering ops translate that to
    ///     <c>false</c>. Eq/Neq still get a useful answer via this path:
    ///     a sentinel will fail their <c>== 0</c>/<c>!= 0</c> tests in the
    ///     direction the caller expects (Eq → false, Neq → true) which is
    ///     fine for type-mismatched equality.
    /// </summary>
    private static int CompareScalar(object? facet, object value)
    {
        if (facet is null) return int.MinValue;

        // Numeric path: if either side parses as decimal, compare both as decimal.
        if (TryToDecimal(facet, out var fd) && TryToDecimal(value, out var vd))
            return fd.CompareTo(vd);

        // Boolean path.
        if (facet is bool fb && value is bool vb)
            return fb.CompareTo(vb);
        if (facet is bool fb2 && value is string vs && bool.TryParse(vs, out var pb))
            return fb2.CompareTo(pb);
        if (value is bool vb2 && facet is string fs && bool.TryParse(fs, out var pb2))
            return pb2.CompareTo(vb2);

        // String fall-back: ordinal compare on the ToString() projections.
        var lhs = ScalarToString(facet);
        var rhs = ScalarToString(value);
        return string.CompareOrdinal(lhs, rhs);
    }

    private static bool TryToDecimal(object? o, out decimal d)
    {
        switch (o)
        {
            case decimal de: d = de; return true;
            case int i: d = i; return true;
            case long l: d = l; return true;
            case short sh: d = sh; return true;
            case byte by: d = by; return true;
            case float f: d = (decimal)f; return true;
            case double db: d = (decimal)db; return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                d = parsed; return true;
            default:
                d = 0m; return false;
        }
    }

    private static string ScalarToString(object? o) => o switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        decimal de => de.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => o.ToString() ?? string.Empty
    };

    // -------- Set operators --------

    /// <summary>
    ///     <c>facet in (a, b, c)</c>: the facet's string projection must equal
    ///     one of the list entries. The list is the <c>string[]</c> produced
    ///     by the parser.
    /// </summary>
    private static bool InList(object? facet, object value)
    {
        if (value is not string[] list) return false;
        var probe = ScalarToString(facet);
        foreach (var item in list)
            if (string.Equals(item, probe, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    ///     <c>facet any in (a, b, c)</c>: the facet must be an enumerable
    ///     (string[]/List/etc.) whose ToString() projections intersect the
    ///     list.
    /// </summary>
    private static bool AnyIn(object? facet, object value)
    {
        if (value is not string[] list || list.Length == 0) return false;
        var set = new HashSet<string>(list, StringComparer.Ordinal);
        foreach (var item in EnumerateFacet(facet))
            if (set.Contains(item))
                return true;
        return false;
    }

    /// <summary>
    ///     <c>facet all in (a, b, c)</c>: every entry the facet projects to
    ///     must appear in the list. An empty facet returns <c>false</c> --
    ///     "all of nothing" isn't useful for policy decisions.
    /// </summary>
    private static bool AllIn(object? facet, object value)
    {
        if (value is not string[] list) return false;
        var set = new HashSet<string>(list, StringComparer.Ordinal);
        var any = false;
        foreach (var item in EnumerateFacet(facet))
        {
            any = true;
            if (!set.Contains(item))
                return false;
        }
        return any;
    }

    private static IEnumerable<string> EnumerateFacet(object? facet)
    {
        switch (facet)
        {
            case null:
                yield break;
            case string s:
                yield return s;
                yield break;
            case IEnumerable seq when facet is not string:
                foreach (var item in seq)
                    yield return ScalarToString(item);
                yield break;
            default:
                yield return ScalarToString(facet);
                yield break;
        }
    }

    // -------- Between / Matches / Contains --------

    /// <summary>
    ///     <c>facet between LO and HI</c>: the parser stores bounds as a
    ///     <c>string[] { lo, hi }</c>. Numeric facets compare numerically;
    ///     anything else falls back to ordinal string compare. Inclusive on
    ///     both ends.
    /// </summary>
    private static bool Between(object? facet, object value)
    {
        if (value is not string[] bounds || bounds.Length != 2) return false;

        if (TryToDecimal(facet, out var fd)
            && decimal.TryParse(bounds[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var lo)
            && decimal.TryParse(bounds[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var hi))
        {
            return fd >= lo && fd <= hi;
        }

        var s = ScalarToString(facet);
        return string.CompareOrdinal(s, bounds[0]) >= 0
               && string.CompareOrdinal(s, bounds[1]) <= 0;
    }

    /// <summary>
    ///     <c>facet matches "pattern"</c>: regex match against the facet's
    ///     string projection. A malformed pattern returns <c>false</c>.
    ///     Caching the compiled regex is deferred to a later optimisation
    ///     pass.
    /// </summary>
    private static bool Matches(object? facet, object value)
    {
        if (facet is null) return false;
        var pattern = ScalarToString(value);
        if (pattern.Length == 0) return false;
        try
        {
            return Regex.IsMatch(ScalarToString(facet), pattern);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     <c>facet contains "needle"</c>: case-sensitive substring on the
    ///     facet's string projection.
    /// </summary>
    private static bool Contains(object? facet, object value)
    {
        if (facet is null) return false;
        return ScalarToString(facet).Contains(ScalarToString(value), StringComparison.Ordinal);
    }
}
