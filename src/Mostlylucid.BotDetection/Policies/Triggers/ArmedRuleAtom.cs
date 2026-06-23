namespace Mostlylucid.BotDetection.Policies.Triggers;

/// <summary>
///     Per-rule armed-state state machine. Tracks the sustained-true and
///     sustained-false durations the trigger service feeds in once per tick.
///     The atom is the single source of truth for whether a rule is ARMED.
///
///     <para>
///         <see cref="Observe"/> is the only mutator. The
///         <c>MeterTriggerService</c> calls it once per tick with the
///         freshly-evaluated predicate result, the current wall-clock instant,
///         and the rule's <see cref="Rules.RuleTriggerOptions.EffectiveSustainFor"/>
///         + <see cref="Rules.RuleTriggerOptions.EffectiveRecoverAfter"/>. The
///         return value indicates whether the armed state transitioned this
///         call -- the registry uses it to raise observability events.
///     </para>
///
///     <para>
///         Concurrency: state mutates under a private gate; read-only
///         properties take the same gate. Atoms are long-lived (one per rule
///         id) and contention is minimal, so a simple lock is preferable to
///         the complexity of interlocked rolls.
///     </para>
/// </summary>
public sealed class ArmedRuleAtom
{
    private readonly object _gate = new();
    private bool _armed;
    private DateTimeOffset? _armedAt;
    private DateTimeOffset? _recoveredAt;
    private int _armCount;
    private int _recoverCount;
    private DateTimeOffset? _firstObservedTrueAt;
    private DateTimeOffset? _firstObservedFalseAt;

    /// <summary><c>true</c> when the rule is currently armed.</summary>
    public bool IsArmed
    {
        get { lock (_gate) return _armed; }
    }

    /// <summary>Wall-clock instant of the most recent ARM transition; <c>null</c> until the rule has armed at least once.</summary>
    public DateTimeOffset? ArmedAt
    {
        get { lock (_gate) return _armedAt; }
    }

    /// <summary>Wall-clock instant of the most recent RECOVER transition; <c>null</c> until the rule has recovered at least once.</summary>
    public DateTimeOffset? RecoveredAt
    {
        get { lock (_gate) return _recoveredAt; }
    }

    /// <summary>Cumulative number of times the rule transitioned UNARMED -> ARMED.</summary>
    public int ArmCount
    {
        get { lock (_gate) return _armCount; }
    }

    /// <summary>Cumulative number of times the rule transitioned ARMED -> UNARMED.</summary>
    public int RecoverCount
    {
        get { lock (_gate) return _recoverCount; }
    }

    /// <summary>
    ///     Feed in one tick of state. Returns <c>true</c> when the armed state
    ///     TRANSITIONS this call (for logging / observability); <c>false</c>
    ///     when the call leaves the state machine unchanged.
    /// </summary>
    /// <param name="predicateNowTrue">
    ///     The freshly-evaluated predicate result against the current meter
    ///     snapshot.
    /// </param>
    /// <param name="now">
    ///     Wall-clock instant of this tick. Passed in rather than read from
    ///     <see cref="DateTimeOffset.UtcNow"/> so deterministic tests can drive
    ///     the state machine via a fake clock.
    /// </param>
    /// <param name="sustainFor">
    ///     How long the predicate must stay continuously true before the rule
    ///     transitions to ARMED.
    /// </param>
    /// <param name="recoverAfter">
    ///     How long the predicate must stay continuously false before the rule
    ///     transitions back to UNARMED.
    /// </param>
    public bool Observe(bool predicateNowTrue, DateTimeOffset now, TimeSpan sustainFor, TimeSpan recoverAfter)
    {
        lock (_gate)
        {
            if (predicateNowTrue)
            {
                // The false-streak clock resets the moment we observe a true.
                _firstObservedFalseAt = null;
                _firstObservedTrueAt ??= now;
                if (!_armed && now - _firstObservedTrueAt!.Value >= sustainFor)
                {
                    _armed = true;
                    _armedAt = now;
                    _armCount++;
                    return true;
                }
                return false;
            }

            // The true-streak clock resets the moment we observe a false.
            _firstObservedTrueAt = null;
            _firstObservedFalseAt ??= now;
            if (_armed && now - _firstObservedFalseAt!.Value >= recoverAfter)
            {
                _armed = false;
                _recoveredAt = now;
                _recoverCount++;
                return true;
            }
            return false;
        }
    }
}
