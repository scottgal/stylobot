using System.Globalization;
using System.Text;
using Mostlylucid.BotDetection.Policies.Predicate;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Serialises a <see cref="Predicate"/> AST to the canonical lowercase
///     wire format the C6 expression-editor JavaScript expects:
///     <code>
///     { "kind": "and" | "or", "children": [...] }
///     { "kind": "term", "facet": "...", "op": "...", "value": ... }
///     </code>
///     The on-disk JSON (<see cref="PredicateJson"/>) uses C#-record names
///     under a <c>$kind</c> discriminator; that round-trips for storage
///     layers but isn't a good wire shape for the editor's chip renderer.
///     This helper exists so the renderer can stay framework-free vanilla
///     JS without learning about polymorphic discriminators.
/// </summary>
internal static class PolicyStackParseSerialiser
{
    public static string SerialiseAst(Predicate ast)
    {
        var sb = new StringBuilder();
        Write(sb, ast);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Predicate node)
    {
        switch (node)
        {
            case Predicate.And and:
                WriteCombinator(sb, "and", and.Children);
                return;
            case Predicate.Or or:
                WriteCombinator(sb, "or", or.Children);
                return;
            case Predicate.Term term:
                WriteTerm(sb, term);
                return;
            default:
                sb.Append("null");
                return;
        }
    }

    private static void WriteCombinator(StringBuilder sb, string kind, Predicate[] children)
    {
        sb.Append("{\"kind\":\"").Append(kind).Append("\",\"children\":[");
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0) sb.Append(',');
            Write(sb, children[i]);
        }
        sb.Append("]}");
    }

    private static void WriteTerm(StringBuilder sb, Predicate.Term term)
    {
        sb.Append("{\"kind\":\"term\",\"facet\":");
        AppendJsonString(sb, term.Facet);
        sb.Append(",\"op\":\"").Append(OpToString(term.Op)).Append("\",\"value\":");
        AppendValue(sb, term.Value);
        sb.Append('}');
    }

    private static void AppendValue(StringBuilder sb, object value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case string s:
                AppendJsonString(sb, s);
                return;
            case string[] arr:
                sb.Append('[');
                for (var i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendJsonString(sb, arr[i]);
                }
                sb.Append(']');
                return;
            case decimal d:
                sb.Append(d.ToString(CultureInfo.InvariantCulture));
                return;
            case double dd:
                sb.Append(dd.ToString(CultureInfo.InvariantCulture));
                return;
            case int ii:
                sb.Append(ii.ToString(CultureInfo.InvariantCulture));
                return;
            default:
                AppendJsonString(sb, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    private static string OpToString(PredicateOp op) => op switch
    {
        PredicateOp.In => "in",
        PredicateOp.NotIn => "notIn",
        PredicateOp.Eq => "eq",
        PredicateOp.Neq => "neq",
        PredicateOp.Gte => "gte",
        PredicateOp.Gt => "gt",
        PredicateOp.Lte => "lte",
        PredicateOp.Lt => "lt",
        PredicateOp.Between => "between",
        PredicateOp.Matches => "matches",
        PredicateOp.Contains => "contains",
        PredicateOp.AnyIn => "anyIn",
        PredicateOp.AllIn => "allIn",
        _ => op.ToString().ToLowerInvariant()
    };
}
