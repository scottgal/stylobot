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
            case Predicate.Term term:
                WriteTerm(term, sb);
                break;
        }
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
        _ => op.ToString().ToLowerInvariant()
    };
}
