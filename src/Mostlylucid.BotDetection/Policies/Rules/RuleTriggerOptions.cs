namespace Mostlylucid.BotDetection.Policies.Rules;

/// <summary>
///     Marks a <see cref="PolicyRule"/> as oscilloscope-driven -- evaluated by
///     the background MeterTriggerService against meter snapshots on a 1s
///     cadence rather than (or in addition to) the per-request resolver loop.
///
///     <para>
///         When a rule has trigger options AND its predicate references ONLY
///         meter signals (no per-request signals), the runtime classifies it
///         as a "trigger rule" and walks the armed-state machine:
///     </para>
///     <list type="bullet">
///       <item><description>Predicate true for &gt;= <see cref="SustainFor"/> -- rule transitions to ARMED.</description></item>
///       <item><description>Predicate false for &gt;= <see cref="RecoverAfter"/> -- rule transitions to UNARMED.</description></item>
///       <item><description>While ARMED, the rule applies to every request the resolver evaluates whose scope matches.</description></item>
///     </list>
///
///     <para>
///         If <see cref="ActionWhileArmed"/> is non-null, it overrides the
///         rule's normal <see cref="PolicyRule.Action"/> while armed.
///         Otherwise the regular action is used.
///     </para>
///
///     <para>
///         <see cref="EffectiveSustainFor"/> defaults to 30s and
///         <see cref="EffectiveRecoverAfter"/> defaults to 60s (recover &gt; sustain
///         to prevent flap). The defaults engage whenever the supplied positional
///         value is the unset <see cref="TimeSpan"/> sentinel
///         (<see cref="TimeSpan.Zero"/>); an explicit non-zero TimeSpan wins.
///         When the trigger options field is null on the rule, the rule is
///         treated as a regular per-request rule and the trigger machine
///         ignores it.
///     </para>
/// </summary>
/// <param name="SustainFor">
///     How long the predicate must stay continuously true before the rule
///     transitions to ARMED. Pass <see cref="TimeSpan.Zero"/> (the default) to
///     adopt the 30s spec floor; any non-zero value wins via
///     <see cref="EffectiveSustainFor"/>.
/// </param>
/// <param name="RecoverAfter">
///     How long the predicate must stay continuously false before the rule
///     transitions back to UNARMED. Pass <see cref="TimeSpan.Zero"/> to adopt
///     the 60s spec floor; any non-zero value wins via
///     <see cref="EffectiveRecoverAfter"/>. Should be &gt;= sustain to keep the
///     state machine from flapping at threshold.
/// </param>
/// <param name="ActionWhileArmed">
///     Optional override for <see cref="PolicyRule.Action"/>. When non-null,
///     the runtime applies this action while the rule is ARMED instead of the
///     rule's normal action. Used to pair a meter trigger with a Throttle
///     action while keeping the regular action a no-op when unarmed.
/// </param>
public sealed record RuleTriggerOptions(
    TimeSpan SustainFor = default,
    TimeSpan RecoverAfter = default,
    PolicyAction? ActionWhileArmed = null)
{
    /// <summary>Spec default for <see cref="SustainFor"/>: 30 seconds.</summary>
    public static readonly TimeSpan DefaultSustainFor = TimeSpan.FromSeconds(30);

    /// <summary>Spec default for <see cref="RecoverAfter"/>: 60 seconds.</summary>
    public static readonly TimeSpan DefaultRecoverAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Resolved sustain window. Returns <see cref="DefaultSustainFor"/>
    ///     when <see cref="SustainFor"/> is the unset sentinel; otherwise the
    ///     explicit positional value.
    /// </summary>
    public TimeSpan EffectiveSustainFor => SustainFor == default ? DefaultSustainFor : SustainFor;

    /// <summary>
    ///     Resolved recovery window. Returns <see cref="DefaultRecoverAfter"/>
    ///     when <see cref="RecoverAfter"/> is the unset sentinel; otherwise the
    ///     explicit positional value.
    /// </summary>
    public TimeSpan EffectiveRecoverAfter => RecoverAfter == default ? DefaultRecoverAfter : RecoverAfter;
}
