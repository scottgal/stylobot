using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Side-effect-free predicate evaluator that records a per-term outcome
///     trace as it walks. <see cref="PredicateEvaluator"/> returns a single
///     <see cref="bool"/>; <see cref="Trace"/> returns the same verdict plus
///     a <see cref="PredicateTrace"/> the explainer panel renders.
///
///     <para>
///     Match semantics MUST track <see cref="PredicateEvaluator"/> exactly --
///     this is the inspection view of the same logic, not a different
///     evaluator. Numeric coercion, set semantics, regex semantics, and the
///     silent-miss-on-unknown-facet rule are all mirrored.
///     </para>
///
///     <para>
///     Short-circuit behaviour is preserved AND made visible: when an
///     <see cref="Predicate.And"/> child fails, every remaining child is
///     emitted with <c>Matched=false</c>, <c>EvalTicks=0</c>, and
///     <c>FailureReason="skipped -- earlier And child failed"</c>. The dual
///     applies to <see cref="Predicate.Or"/> on the first child that wins.
///     This keeps the trace shape stable -- the explainer always sees a row
///     per term regardless of when the predicate decided.
///     </para>
/// </summary>
public static class PredicateTraceEvaluator
{
    /// <summary>
    ///     Walk <paramref name="predicate"/> against <paramref name="signals"/>
    ///     and return the overall verdict plus an in-order per-term outcome
    ///     trace.
    /// </summary>
    public static PredicateTrace Trace(
        Predicate predicate,
        IReadOnlyDictionary<string, object?> signals)
    {
        var outcomes = new List<PredicateTermOutcome>();
        var matched = Walk(predicate, signals, outcomes, skipReason: null);
        long total = 0;
        for (var i = 0; i < outcomes.Count; i++) total += outcomes[i].EvalTicks;
        return new PredicateTrace(matched, outcomes, total);
    }

    // -------- AST walk --------

    private static bool Walk(
        Predicate node,
        IReadOnlyDictionary<string, object?> signals,
        List<PredicateTermOutcome> outcomes,
        string? skipReason)
    {
        switch (node)
        {
            case Predicate.And and:
                return WalkAnd(and, signals, outcomes, skipReason);
            case Predicate.Or or:
                return WalkOr(or, signals, outcomes, skipReason);
            case Predicate.Term term:
                return WalkTerm(term, signals, outcomes, skipReason);
            default:
                return false;
        }
    }

    private static bool WalkAnd(
        Predicate.And and,
        IReadOnlyDictionary<string, object?> signals,
        List<PredicateTermOutcome> outcomes,
        string? skipReason)
    {
        // Already inside a skipped branch -- every term we surface MUST inherit
        // the parent skip reason. Vacuous truth on an empty And matches
        // PredicateEvaluator.EvaluateAnd, but only when not skipped.
        if (skipReason is not null)
        {
            for (var i = 0; i < and.Children.Length; i++)
                Walk(and.Children[i], signals, outcomes, skipReason);
            return false;
        }

        var passed = true;
        string? carriedSkip = null;
        for (var i = 0; i < and.Children.Length; i++)
        {
            var child = and.Children[i];
            var result = Walk(child, signals, outcomes, carriedSkip);
            if (!result && carriedSkip is null)
            {
                // First failing child trips short-circuit for every following sibling.
                passed = false;
                carriedSkip = "skipped; earlier And child failed";
            }
        }
        return passed;
    }

    private static bool WalkOr(
        Predicate.Or or,
        IReadOnlyDictionary<string, object?> signals,
        List<PredicateTermOutcome> outcomes,
        string? skipReason)
    {
        if (skipReason is not null)
        {
            for (var i = 0; i < or.Children.Length; i++)
                Walk(or.Children[i], signals, outcomes, skipReason);
            return false;
        }

        var anyWon = false;
        string? carriedSkip = null;
        for (var i = 0; i < or.Children.Length; i++)
        {
            var child = or.Children[i];
            var result = Walk(child, signals, outcomes, carriedSkip);
            if (result && !anyWon)
            {
                // First winning child trips short-circuit for every following sibling.
                anyWon = true;
                carriedSkip = "skipped; earlier Or child won";
            }
        }
        return anyWon;
    }

    private static bool WalkTerm(
        Predicate.Term term,
        IReadOnlyDictionary<string, object?> signals,
        List<PredicateTermOutcome> outcomes,
        string? skipReason)
    {
        if (skipReason is not null)
        {
            // Short-circuited: emit a zero-cost skipped outcome and DO NOT
            // touch the signals dictionary -- the trace must reflect that the
            // evaluator never paid the lookup cost.
            outcomes.Add(new PredicateTermOutcome(
                Facet: term.Facet,
                Op: term.Op,
                Value: term.Value,
                ActualFacetValue: null,
                Matched: false,
                EvalTicks: 0,
                FailureReason: skipReason));
            return false;
        }

        var ticks = Stopwatch.GetTimestamp();

        if (!signals.TryGetValue(term.Facet, out var facetValue))
        {
            // Silent miss -- same as PredicateEvaluator.EvaluateTerm.
            var elapsed = Stopwatch.GetTimestamp() - ticks;
            outcomes.Add(new PredicateTermOutcome(
                Facet: term.Facet,
                Op: term.Op,
                Value: term.Value,
                ActualFacetValue: null,
                Matched: false,
                EvalTicks: ToTimeSpanTicks(elapsed),
                FailureReason: "facet missing"));
            return false;
        }

        var matched = EvaluateScalar(term, facetValue, out var failureReason);
        var doneTicks = Stopwatch.GetTimestamp() - ticks;
        outcomes.Add(new PredicateTermOutcome(
            Facet: term.Facet,
            Op: term.Op,
            Value: term.Value,
            ActualFacetValue: facetValue,
            Matched: matched,
            EvalTicks: ToTimeSpanTicks(doneTicks),
            FailureReason: matched ? null : failureReason));
        return matched;
    }

    // -------- Term semantics (mirror PredicateEvaluator) --------

    private static bool EvaluateScalar(Predicate.Term term, object? facetValue, out string failureReason)
    {
        switch (term.Op)
        {
            case PredicateOp.Eq:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c == 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)}; expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.Neq:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c != 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} equals expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.Gte:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c != int.MinValue && c >= 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} < expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.Gt:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c != int.MinValue && c > 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} <= expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.Lte:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c != int.MinValue && c <= 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} > expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.Lt:
                {
                    var c = CompareScalar(facetValue, term.Value);
                    var ok = c != int.MinValue && c < 0;
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} >= expected = {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.In:
                {
                    var ok = InList(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)}; expected one of {RenderList(term.Value)}";
                    return ok;
                }
            case PredicateOp.NotIn:
                {
                    var ok = !InList(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} is in {RenderList(term.Value)}";
                    return ok;
                }
            case PredicateOp.AnyIn:
                {
                    var ok = AnyIn(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"none of {Render(facetValue)} intersect {RenderList(term.Value)}";
                    return ok;
                }
            case PredicateOp.AllIn:
                {
                    var ok = AllIn(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"not all of {Render(facetValue)} are in {RenderList(term.Value)}";
                    return ok;
                }
            case PredicateOp.Between:
                {
                    var ok = Between(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)}; expected between {RenderList(term.Value)}";
                    return ok;
                }
            case PredicateOp.Matches:
                {
                    var ok = Matches(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} does not match /{Render(term.Value)}/";
                    return ok;
                }
            case PredicateOp.Contains:
                {
                    var ok = Contains(facetValue, term.Value);
                    failureReason = ok ? string.Empty : $"actual = {Render(facetValue)} does not contain {Render(term.Value)}";
                    return ok;
                }
            case PredicateOp.InCidr:
                {
                    var ok = InCidr(facetValue, term.Value);
                    failureReason = ok
                        ? string.Empty
                        : term.Value is string[]
                            ? $"actual = {Render(facetValue)} is not in any of {RenderList(term.Value)}"
                            : $"actual = {Render(facetValue)} is not in {Render(term.Value)}";
                    return ok;
                }
            default:
                failureReason = "unknown operator";
                return false;
        }
    }

    // -------- Scalar comparison (duplicated from PredicateEvaluator) --------

    private static int CompareScalar(object? facet, object value)
    {
        if (facet is null) return int.MinValue;

        if (TryToDecimal(facet, out var fd) && TryToDecimal(value, out var vd))
            return fd.CompareTo(vd);

        if (facet is bool fb && value is bool vb)
            return fb.CompareTo(vb);
        if (facet is bool fb2 && value is string vs && bool.TryParse(vs, out var pb))
            return fb2.CompareTo(pb);
        if (value is bool vb2 && facet is string fs && bool.TryParse(fs, out var pb2))
            return pb2.CompareTo(vb2);

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

    private static bool InList(object? facet, object value)
    {
        if (value is not string[] list) return false;
        var probe = ScalarToString(facet);
        foreach (var item in list)
            if (string.Equals(item, probe, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool AnyIn(object? facet, object value)
    {
        if (value is not string[] list || list.Length == 0) return false;
        var set = new HashSet<string>(list, StringComparer.Ordinal);
        foreach (var item in EnumerateFacet(facet))
            if (set.Contains(item))
                return true;
        return false;
    }

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

    private static bool Contains(object? facet, object value)
    {
        if (facet is null) return false;
        return ScalarToString(facet).Contains(ScalarToString(value), StringComparison.Ordinal);
    }

    // Mirrors PredicateEvaluator.InCidr exactly -- silent-miss on every
    // malformed-input arm, single + list value shapes, delegates to the same
    // CidrHelper.IsInSubnet that owns the parse + mask arithmetic.
    private static bool InCidr(object? facet, object value)
    {
        if (facet is not string ipStr || string.IsNullOrWhiteSpace(ipStr))
            return false;

        switch (value)
        {
            case string single:
                return Helpers.CidrHelper.IsInSubnet(ipStr, single);
            case string[] many:
                foreach (var cidr in many)
                    if (Helpers.CidrHelper.IsInSubnet(ipStr, cidr))
                        return true;
                return false;
            default:
                return false;
        }
    }

    // -------- Failure-reason rendering helpers --------

    private static string Render(object? o)
    {
        if (o is null) return "<missing>";
        if (o is string[] arr) return RenderList(o);
        return ScalarToString(o);
    }

    private static string RenderList(object value)
    {
        if (value is not string[] arr) return ScalarToString(value);
        var sb = new StringBuilder("(");
        for (var i = 0; i < arr.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(arr[i]);
        }
        sb.Append(')');
        return sb.ToString();
    }

    // Stopwatch.GetTimestamp returns Stopwatch ticks; we expose TimeSpan ticks
    // so the explainer's per-term latency can be turned into microseconds with
    // the same /10 divisor PolicyDecision.EvalLatencyTicks uses.
    private static long ToTimeSpanTicks(long stopwatchTicks)
    {
        if (stopwatchTicks <= 0) return 0;
        // (stopwatchTicks * TimeSpan.TicksPerSecond) / Stopwatch.Frequency.
        // The intermediate may overflow long for very large stopwatchTicks,
        // but term evaluation is sub-millisecond -- the input stays small.
        return (long)((double)stopwatchTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }
}
