using System.Globalization;
using System.Text;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Renders a <see cref="Predicate"/> AST as the canonical text form
///     that <see cref="PredicateParser.Parse"/> consumes. Round-trip is
///     exact for terms produced by the parser; manually constructed
///     trees that mix incompatible value/operator combinations are
///     emitted as best-effort.
/// </summary>
public static class PredicateFormatter
{
    public static string Format(Predicate predicate)
    {
        var sb = new StringBuilder();
        Write(predicate, sb, parentPrecedence: 0);
        return sb.ToString();
    }

    private static void Write(Predicate node, StringBuilder sb, int parentPrecedence)
    {
        switch (node)
        {
            case Predicate.Or or:
                WriteCombinator(or.Children, "or", precedence: 1, sb, parentPrecedence);
                break;
            case Predicate.And and:
                WriteCombinator(and.Children, "and", precedence: 2, sb, parentPrecedence);
                break;
            case Predicate.Sustain sustain:
                WriteSustain(sustain, sb, parentPrecedence);
                break;
            case Predicate.Term term:
                WriteTerm(term, sb);
                break;
        }
    }

    /// <summary>
    ///     Format a <see cref="Predicate.Sustain"/> as <c>&lt;inner&gt; for &lt;duration&gt;</c>.
    ///     Inner is written at the And precedence (2) so a composite inner
    ///     <see cref="Predicate.Or"/> gets parenthesised consistently with
    ///     "or inside and" rendering -- a Sustain over an Or otherwise reads
    ///     ambiguously.
    /// </summary>
    private static void WriteSustain(Predicate.Sustain sustain, StringBuilder sb, int parentPrecedence)
    {
        // Sustain associates tighter than the surrounding boolean combinators:
        // it's a unit-level decorator on top of an atom/group. Render the inner
        // at And precedence so an Or inside picks up parens, mirroring
        // WriteCombinator's "Or inside And" rule.
        Write(sustain.Inner, sb, parentPrecedence: 2);
        sb.Append(" for ");
        sb.Append(FormatDuration(sustain.Duration));
    }

    /// <summary>
    ///     Emit a duration using the same shorthand
    ///     <see cref="Mostlylucid.BotDetection.Policies.Rules.DurationParser"/>
    ///     accepts: prefer a single-unit token (<c>30s</c>, <c>5m</c>,
    ///     <c>1h</c>, <c>1d</c>) when the value is an integer in that unit;
    ///     otherwise compose <c>{d}d{h}h{m}m{s}s</c> dropping zero-valued
    ///     groups. Sub-second precision falls back to a single
    ///     fractional-second token (<c>1.5s</c>).
    /// </summary>
    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "0s";

        // Single-unit fast path: round down to the largest whole unit.
        if (duration.Ticks % TimeSpan.TicksPerDay == 0)
            return ((long)duration.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
        if (duration.Ticks % TimeSpan.TicksPerHour == 0)
            return ((long)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        if (duration.Ticks % TimeSpan.TicksPerMinute == 0)
            return ((long)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        if (duration.Ticks % TimeSpan.TicksPerSecond == 0)
            return ((long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";

        // Composite shorthand for mixed integer groups (e.g. 1h30m).
        var days = duration.Days;
        var hours = duration.Hours;
        var minutes = duration.Minutes;
        var seconds = duration.Seconds;
        if (duration.Ticks % TimeSpan.TicksPerSecond == 0
            && (days > 0 || hours > 0 || minutes > 0 || seconds > 0))
        {
            var sb = new StringBuilder();
            if (days > 0) sb.Append(days.ToString(CultureInfo.InvariantCulture)).Append('d');
            if (hours > 0) sb.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('h');
            if (minutes > 0) sb.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('m');
            if (seconds > 0) sb.Append(seconds.ToString(CultureInfo.InvariantCulture)).Append('s');
            return sb.ToString();
        }

        // Sub-second remainder -- emit a fractional-second token.
        return duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s";
    }

    // Parenthesise only when the child precedence is lower than parent. "OR" inside "AND"
    // needs grouping; "AND" inside "OR" does not.
    private static void WriteCombinator(Predicate[] children, string keyword, int precedence,
        StringBuilder sb, int parentPrecedence)
    {
        var needParens = precedence < parentPrecedence;
        if (needParens) sb.Append('(');
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0) sb.Append(' ').Append(keyword).Append(' ');
            Write(children[i], sb, precedence);
        }
        if (needParens) sb.Append(')');
    }

    private static void WriteTerm(Predicate.Term term, StringBuilder sb)
    {
        sb.Append(term.Facet);
        sb.Append(' ').Append(OpText(term.Op)).Append(' ');
        WriteValue(term.Value, term.Op, sb);
    }

    private static void WriteValue(object value, PredicateOp op, StringBuilder sb)
    {
        // Between renders as "<lo> and <hi>"; we store the bounds as string[].
        if (op == PredicateOp.Between && value is string[] bounds && bounds.Length == 2)
        {
            sb.Append(bounds[0]).Append(' ').Append("and").Append(' ').Append(bounds[1]);
            return;
        }

        if (value is string[] arr)
        {
            sb.Append('(');
            for (var i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(arr[i]);
            }
            sb.Append(')');
            return;
        }

        sb.Append(value switch
        {
            bool b => b ? "true" : "false",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            null => "null",
            _ => value.ToString() ?? string.Empty
        });
    }

    private static string OpText(PredicateOp op) => op switch
    {
        PredicateOp.In => "in",
        PredicateOp.NotIn => "not in",
        PredicateOp.Eq => "=",
        PredicateOp.Neq => "!=",
        PredicateOp.Gte => ">=",
        PredicateOp.Gt => ">",
        PredicateOp.Lte => "<=",
        PredicateOp.Lt => "<",
        PredicateOp.Between => "between",
        PredicateOp.Matches => "matches",
        PredicateOp.Contains => "contains",
        PredicateOp.AnyIn => "any in",
        PredicateOp.AllIn => "all in",
        PredicateOp.InCidr => "in_cidr",
        _ => op.ToString().ToLowerInvariant()
    };
}
