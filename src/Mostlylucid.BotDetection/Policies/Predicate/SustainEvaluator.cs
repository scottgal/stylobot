using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Owns the registry of <see cref="SustainTermAtom"/> instances keyed by
///     <c>(ruleId, inner-AST-hash)</c>. Stateless callers ask whether a
///     <see cref="Predicate.Sustain"/> node is currently satisfied; the
///     evaluator looks up (or creates) the atom and observes the current
///     inner-truth value. The time source is injected for testability so
///     <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>-style
///     fakes can drive transitions deterministically.
///
///     <para>
///         Atom storage is purely in-memory. Sustain state is runtime by design:
///         a process restart clears every atom and predicates re-arm naturally
///         on the next observed true. The spec for T-Expr explicitly accepts
///         this -- persisting Sustain state would require a durable Phase G-style
///         armed-rule registry, which is the operator escalation path, not the
///         expression-language semantics.
///     </para>
/// </summary>
public sealed class SustainEvaluator
{
    private readonly ConcurrentDictionary<(Guid RuleId, int InnerHash), SustainTermAtom> _atoms = new();
    private readonly TimeProvider _timeProvider;

    public SustainEvaluator() : this(timeProvider: null)
    {
    }

    public SustainEvaluator(TimeProvider? timeProvider)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    ///     Evaluate a <see cref="Predicate.Sustain"/> node against the current
    ///     signal map. The atom lookup keys on the inner AST hash, so two
    ///     structurally identical <c>FOR</c> terms inside the same rule share
    ///     state -- usually desirable (one operator concept, one timer); two
    ///     different rules each get their own atom regardless of inner shape.
    /// </summary>
    public bool IsSatisfied(
        Guid ruleId,
        Predicate.Sustain node,
        IReadOnlyDictionary<string, object?> signals)
    {
        var key = (ruleId, PredicateHasher.Hash(node.Inner));
        var atom = _atoms.GetOrAdd(key, static _ => new SustainTermAtom());
        var innerTrue = PredicateEvaluator.EvaluateStateless(node.Inner, signals);
        return atom.IsSatisfied(innerTrue, _timeProvider.GetUtcNow(), node.Duration);
    }

    /// <summary>Snapshot atom count -- diagnostics / tests.</summary>
    public int AtomCount => _atoms.Count;

    /// <summary>
    ///     Test/diagnostic accessor. Returns the atom for a given
    ///     <c>(ruleId, innerHash)</c> key or <c>null</c> if no observation
    ///     has been made yet. Public so tests can inspect first-observed-true
    ///     timestamps without going through the evaluator.
    /// </summary>
    internal SustainTermAtom? TryGetAtom(Guid ruleId, Predicate inner)
        => _atoms.TryGetValue((ruleId, PredicateHasher.Hash(inner)), out var atom) ? atom : null;

    /// <summary>
    ///     Drop atoms for a rule -- called when a rule is removed/disabled so
    ///     its sustain state doesn't linger. Not auto-invoked; future work
    ///     wires this through the policy mutation paths.
    /// </summary>
    public int RemoveForRule(Guid ruleId)
    {
        var removed = 0;
        foreach (var key in _atoms.Keys)
        {
            if (key.RuleId == ruleId && _atoms.TryRemove(key, out _))
                removed++;
        }
        return removed;
    }
}
