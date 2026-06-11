namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Deterministic structural hash over a <see cref="Predicate"/> AST.
///     Used by <see cref="SustainEvaluator"/> to key per-(rule, term) sustain
///     atoms by the wrapped inner predicate so two identical
///     <see cref="Predicate.Sustain"/> nodes inside the same rule share state.
///
///     <para>
///         The hash combines the variant tag with its constituent fields;
///         collisions are theoretically possible but irrelevant in practice
///         because <see cref="SustainEvaluator"/> already partitions atoms by
///         <see cref="System.Guid"/> rule id, leaving the inner hash to
///         distinguish only sibling Sustain terms inside ONE rule. Two
///         predicate variants within one rule producing the same hash would
///         have to be structurally identical, in which case sharing the atom
///         is the correct behaviour.
///     </para>
/// </summary>
public static class PredicateHasher
{
    /// <summary>
    ///     Walk <paramref name="predicate"/> recursively and return a
    ///     structural hash. Returns the same value for two ASTs whose
    ///     <see cref="Predicate"/> records are <see cref="object.Equals(object)"/>-equal.
    /// </summary>
    public static int Hash(Predicate predicate)
    {
        var hc = new HashCode();
        Walk(predicate, ref hc);
        return hc.ToHashCode();
    }

    private static void Walk(Predicate node, ref HashCode hc)
    {
        switch (node)
        {
            case Predicate.And and:
                hc.Add(1);
                hc.Add(and.Children.Length);
                foreach (var child in and.Children) Walk(child, ref hc);
                break;
            case Predicate.Or or:
                hc.Add(2);
                hc.Add(or.Children.Length);
                foreach (var child in or.Children) Walk(child, ref hc);
                break;
            case Predicate.Sustain sustain:
                hc.Add(3);
                hc.Add(sustain.Duration);
                Walk(sustain.Inner, ref hc);
                break;
            case Predicate.Term term:
                hc.Add(4);
                hc.Add(term.Facet, StringComparer.Ordinal);
                hc.Add(term.Op);
                hc.Add(ValueHash(term.Value));
                break;
            default:
                hc.Add(0);
                break;
        }
    }

    // Mirrors the Value-equality treatment in Predicate.Term so a string[]
    // value isn't reduced to a reference identity hash.
    private static int ValueHash(object? value)
    {
        switch (value)
        {
            case null: return 0;
            case string[] sa:
                var hc = new HashCode();
                hc.Add(-1);
                foreach (var s in sa) hc.Add(s, StringComparer.Ordinal);
                return hc.ToHashCode();
            default: return value.GetHashCode();
        }
    }
}
