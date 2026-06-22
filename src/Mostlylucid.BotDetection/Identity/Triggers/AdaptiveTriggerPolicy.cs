using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Identity.Triggers;

/// <summary>
///     Composable trigger policy that replaces fixed wall-clock gates with a
///     four-axis decision: floor (min interval), safety net (max interval),
///     ceiling (load band), and any-of signal thresholds. Designed to be
///     evaluated on a fast heartbeat (e.g. <c>TickCadence.Tick1s</c>) by each
///     subscriber; the subscriber's actual work only runs when the policy says
///     yes. See <c>docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md</c>.
///
///     <para>
///         <b>Eval rule:</b>
///         <list type="number">
///             <item>If <see cref="MinInterval"/> hasn't elapsed: NO.</item>
///             <item>If <see cref="MaxInterval"/> is set and exceeded: YES (safety net).</item>
///             <item>If current band &gt; <see cref="MaxBand"/>: NO.</item>
///             <item>If any <see cref="AnyOfSignals"/> condition is met: YES.</item>
///             <item>Otherwise: NO.</item>
///         </list>
///     </para>
/// </summary>
public sealed record AdaptiveTriggerPolicy
{
    /// <summary>
    ///     Below this elapsed time we never run. Prevents thrashing under
    ///     bursty signal pressure. Default 30 s.
    /// </summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Above this elapsed time we always run, ignoring all other gates.
    ///     The safety net so a perpetually busy system still calibrates. Null
    ///     disables the safety net (run only on signal). Default 6 h.
    /// </summary>
    public TimeSpan? MaxInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     Highest <see cref="LoadBand"/> we'll still fire at. Above this, the
    ///     system is too busy to spare CPU. Default Normal (i.e. don't run at
    ///     High or Critical). Read from <see cref="ILoadBandSource"/>.
    /// </summary>
    public LoadBand MaxBand { get; init; } = LoadBand.Normal;

    /// <summary>
    ///     OR-of signal thresholds. Each condition reads a named signal from
    ///     the <see cref="TriggerContext.Signals"/> snapshot and fires when the
    ///     observed value &gt;= the threshold. Empty list = time-only trigger
    ///     (runs at <see cref="MinInterval"/> cadence subject to band ceiling).
    /// </summary>
    public IReadOnlyList<SignalCondition> AnyOfSignals { get; init; } = Array.Empty<SignalCondition>();

    /// <summary>
    ///     Whether the trigger is active. When false, <see cref="AdaptiveTriggerEvaluator"/>
    ///     always returns <see cref="TriggerDecision.NoBecause"/> with reason "disabled".
    ///     Lets a service stay registered but dormant without a separate enable flag.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>One signal threshold inside an <see cref="AdaptiveTriggerPolicy"/>'s AnyOf set.</summary>
/// <param name="Key">Signal name. Resolved against <see cref="TriggerContext.Signals"/> at eval time.</param>
/// <param name="Threshold">Value at or above which the condition fires.</param>
public readonly record struct SignalCondition(string Key, double Threshold);

