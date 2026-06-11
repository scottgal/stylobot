namespace Mostlylucid.BotDetection.Policies.Predicate;

/// <summary>
///     Per-(rule, term) sustain state. Tracks the first time the inner predicate
///     was observed true; returns <see cref="IsSatisfied"/> when that "true" has
///     been sustained for at least the configured duration. Resets to
///     "not observed" on the first false observation, so the atom re-arms
///     naturally on the next true.
///
///     <para>
///         Single-writer by convention -- one <see cref="SustainEvaluator" />
///         owns one atom per <c>(ruleId, innerHash)</c> key and observations
///         arrive on the request/tick thread that the resolver/trigger
///         service runs on. The internal lock is defensive against accidental
///         multi-writer use (e.g. a misconfigured host that runs two trigger
///         services against the same registry); the cost of the
///         uncontended lock is negligible against the inner predicate's
///         dictionary lookup.
///     </para>
/// </summary>
public sealed class SustainTermAtom
{
    private readonly object _gate = new();
    private DateTimeOffset? _firstObservedTrueAt;

    /// <summary>
    ///     Feed the current truth value of the wrapped inner predicate at
    ///     <paramref name="now"/> and return whether the sustain condition is
    ///     met. <paramref name="duration"/> is read on every call so the
    ///     evaluator can change it without rebuilding the atom (the duration
    ///     lives on the AST node, not on the atom).
    /// </summary>
    public bool IsSatisfied(bool innerCurrentlyTrue, DateTimeOffset now, TimeSpan duration)
    {
        lock (_gate)
        {
            if (!innerCurrentlyTrue)
            {
                _firstObservedTrueAt = null;
                return false;
            }
            _firstObservedTrueAt ??= now;
            return now - _firstObservedTrueAt.Value >= duration;
        }
    }

    /// <summary>Test-only -- inspect the first-observed-true timestamp.</summary>
    internal DateTimeOffset? FirstObservedTrueAt
    {
        get { lock (_gate) return _firstObservedTrueAt; }
    }
}
