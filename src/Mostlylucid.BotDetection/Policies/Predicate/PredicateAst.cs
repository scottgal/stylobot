using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Operators for <see cref="Predicate.Term"/> predicates.
///     Facet values use the existing <c>SignalKeys</c> vocabulary.
/// </summary>
public enum PredicateOp
{
    In,
    NotIn,
    Eq,
    Neq,
    Gte,
    Gt,
    Lte,
    Lt,
    Between,
    Matches,
    Contains,
    AnyIn,
    AllIn
}

/// <summary>
///     Polymorphic predicate AST node. Persisted as JSON (YAML on FOSS,
///     JSONB on commercial Postgres) using a <c>$kind</c> discriminator
///     so storage layers don't need to know the concrete subtypes.
/// </summary>
[JsonConverter(typeof(PredicatePolymorphicConverter))]
public abstract record Predicate
{
    /// <summary>
    ///     Logical AND over an ordered list of child predicates. Evaluates
    ///     true when every child evaluates true.
    /// </summary>
    public sealed record And(Predicate[] Children) : Predicate
    {
        public bool Equals(And? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Children.Length == other.Children.Length
                   && Children.SequenceEqual(other.Children);
        }

        public override int GetHashCode()
        {
            var hc = new HashCode();
            foreach (var child in Children) hc.Add(child);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    ///     Logical OR over an ordered list of child predicates. Evaluates
    ///     true when at least one child evaluates true.
    /// </summary>
    public sealed record Or(Predicate[] Children) : Predicate
    {
        public bool Equals(Or? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Children.Length == other.Children.Length
                   && Children.SequenceEqual(other.Children);
        }

        public override int GetHashCode()
        {
            var hc = new HashCode();
            foreach (var child in Children) hc.Add(child);
            return hc.ToHashCode();
        }
    }

    /// <summary>
    ///     Leaf predicate: <c>(Facet Op Value)</c>. <c>Facet</c> is a
    ///     SignalKeys constant (e.g. <c>"bot.type"</c>,
    ///     <c>"score.bot_probability"</c>). <c>Value</c> is a primitive
    ///     (bool, decimal, string) or a <c>string[]</c> for set
    ///     operators (<see cref="PredicateOp.In"/>, <see cref="PredicateOp.NotIn"/>,
    ///     <see cref="PredicateOp.AnyIn"/>, <see cref="PredicateOp.AllIn"/>).
    /// </summary>
    public sealed record Term(string Facet, PredicateOp Op, object Value) : Predicate
    {
        public bool Equals(Term? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            if (!string.Equals(Facet, other.Facet, StringComparison.Ordinal)) return false;
            if (Op != other.Op) return false;
            return ValueEquals(Value, other.Value);
        }

        public override int GetHashCode()
        {
            var hc = new HashCode();
            hc.Add(Facet, StringComparer.Ordinal);
            hc.Add(Op);
            hc.Add(ValueHashCode(Value));
            return hc.ToHashCode();
        }

        // String[] is compared by reference under default record equality; use
        // SequenceEqual to keep round-trip semantics deterministic.
        private static bool ValueEquals(object? a, object? b)
        {
            if (a is null && b is null) return true;
            if (a is null || b is null) return false;
            if (a is string[] sa && b is string[] sb)
                return sa.SequenceEqual(sb, StringComparer.Ordinal);
            return a.Equals(b);
        }

        private static int ValueHashCode(object? v)
        {
            if (v is null) return 0;
            if (v is string[] sa)
            {
                var hc = new HashCode();
                foreach (var s in sa) hc.Add(s, StringComparer.Ordinal);
                return hc.ToHashCode();
            }
            return v.GetHashCode();
        }
    }
}

/// <summary>
///     Pre-configured <see cref="JsonSerializerOptions"/> for predicate
///     serialisation. Single instance kept on a static so callers don't
///     allocate a new converter chain per round-trip.
/// </summary>
public static class PredicateJson
{
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        opts.Converters.Add(new PredicatePolymorphicConverter());
        return opts;
    }
}

/// <summary>
///     Polymorphic JSON converter for <see cref="Predicate"/>. Wire format
///     uses a <c>$kind</c> discriminator: <c>"And"</c>/<c>"Or"</c> carry a
///     <c>children</c> array; <c>"Term"</c> carries <c>facet</c>,
///     <c>op</c>, <c>value</c>.
/// </summary>
public sealed class PredicatePolymorphicConverter : JsonConverter<Predicate>
{
    public override Predicate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Buffer the object so $kind doesn't have to be the first property -- robust
        // to storage layers (Postgres jsonb, user-edited YAML→JSON) that may reorder.
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("$kind", out var kindProp))
            throw new JsonException("Predicate JSON is missing required '$kind' discriminator.");

        var kind = kindProp.GetString() ?? string.Empty;

        return kind switch
        {
            "And" => new Predicate.And(ReadChildren(root, options)),
            "Or" => new Predicate.Or(ReadChildren(root, options)),
            "Term" => ReadTerm(root),
            _ => throw new JsonException(
                $"Unknown predicate kind '{kind}'. Expected one of: And, Or, Term.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Predicate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case Predicate.And and:
                writer.WriteString("$kind", "And");
                WriteChildren(writer, and.Children, options);
                break;

            case Predicate.Or or:
                writer.WriteString("$kind", "Or");
                WriteChildren(writer, or.Children, options);
                break;

            case Predicate.Term term:
                writer.WriteString("$kind", "Term");
                writer.WriteString("facet", term.Facet);
                writer.WriteString("op", OpToString(term.Op));
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, term.Value, term.Value.GetType(), options);
                break;

            default:
                throw new JsonException($"Unhandled Predicate subtype: {value.GetType().Name}");
        }
        writer.WriteEndObject();
    }

    private static void WriteChildren(Utf8JsonWriter writer, Predicate[] children, JsonSerializerOptions options)
    {
        writer.WritePropertyName("children");
        writer.WriteStartArray();
        foreach (var child in children)
            JsonSerializer.Serialize(writer, child, options);
        writer.WriteEndArray();
    }

    private static Predicate[] ReadChildren(JsonElement root, JsonSerializerOptions options)
    {
        if (!root.TryGetProperty("children", out var childrenProp))
            return Array.Empty<Predicate>();

        var list = new List<Predicate>();
        foreach (var item in childrenProp.EnumerateArray())
        {
            var child = JsonSerializer.Deserialize<Predicate>(item.GetRawText(), options)
                        ?? throw new JsonException("Null predicate child in array.");
            list.Add(child);
        }
        return list.ToArray();
    }

    private static Predicate.Term ReadTerm(JsonElement root)
    {
        var facet = root.TryGetProperty("facet", out var fp) ? fp.GetString() ?? string.Empty : string.Empty;

        var op = PredicateOp.Eq;
        if (root.TryGetProperty("op", out var opProp))
            op = StringToOp(opProp.GetString() ?? "eq");

        object value = string.Empty;
        if (root.TryGetProperty("value", out var valueProp))
            value = DeserialiseValue(valueProp);

        return new Predicate.Term(facet, op, value);
    }

    // Numbers come back as decimal -- the test asserts equality against literal 0.7m
    // and 0.8m, and double would lose precision.
    private static object DeserialiseValue(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.GetDecimal(),
        JsonValueKind.String => el.GetString() ?? string.Empty,
        JsonValueKind.Array => ReadStringArray(el),
        _ => el.GetRawText()
    };

    private static string[] ReadStringArray(JsonElement el)
    {
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            list.Add(item.GetString() ?? string.Empty);
        return list.ToArray();
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

    private static PredicateOp StringToOp(string s) => s switch
    {
        "in" => PredicateOp.In,
        "notIn" => PredicateOp.NotIn,
        "eq" => PredicateOp.Eq,
        "neq" => PredicateOp.Neq,
        "gte" => PredicateOp.Gte,
        "gt" => PredicateOp.Gt,
        "lte" => PredicateOp.Lte,
        "lt" => PredicateOp.Lt,
        "between" => PredicateOp.Between,
        "matches" => PredicateOp.Matches,
        "contains" => PredicateOp.Contains,
        "anyIn" => PredicateOp.AnyIn,
        "allIn" => PredicateOp.AllIn,
        _ => Enum.TryParse<PredicateOp>(s, true, out var v) ? v : PredicateOp.Eq
    };
}
