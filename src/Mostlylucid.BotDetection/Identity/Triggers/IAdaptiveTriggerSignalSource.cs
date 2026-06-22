namespace Mostlylucid.BotDetection.Identity.Triggers;

/// <summary>
///     Pluggable signal source for an <see cref="AdaptiveTriggerPolicy"/>.
///     Each subscriber wires its own concrete implementation -- calibration
///     reads observation-count + drift, well-known-bots refresh reads nothing,
///     path-lifecycle flush reads dirty-row count. Snapshot is called once
///     per heartbeat; Reset is called by the subscriber AFTER it commits a
///     successful run so concurrent obs during the run count toward the next
///     cycle rather than being silently discarded.
///
///     <para>
///         Implementations MUST be thread-safe: signal writes happen on every
///         request hot path; the snapshot read happens on a tick.
///     </para>
/// </summary>
public interface IAdaptiveTriggerSignalSource
{
    /// <summary>
    ///     Point-in-time read of the current signal values. The returned
    ///     dictionary may be a defensive copy or a stable read of an
    ///     interlocked-managed backing store; either way callers must not
    ///     mutate it.
    /// </summary>
    IReadOnlyDictionary<string, double> Snapshot();

    /// <summary>
    ///     Clear accumulated signals. Called by the subscriber AFTER its
    ///     gated work has run successfully so the next cycle counts only
    ///     post-run pressure. Idempotent -- the calibration loop may reset
    ///     more than once if it picks up multiple heartbeats in a single
    ///     successful pass.
    /// </summary>
    void Reset();
}

/// <summary>
///     Null-object implementation. Used when a subscriber's trigger is purely
///     time-based (no signal axis) or when the DI container hasn't wired a
///     real source. Always returns an empty snapshot so any
///     <see cref="SignalCondition"/> evaluates to false on the
///     <see cref="System.Collections.Generic.IReadOnlyDictionary{TKey, TValue}.TryGetValue"/>
///     miss branch -- the trigger falls back to its min/max interval gates.
/// </summary>
public sealed class NullAdaptiveTriggerSignalSource : IAdaptiveTriggerSignalSource
{
    public static readonly NullAdaptiveTriggerSignalSource Instance = new();
    private static readonly IReadOnlyDictionary<string, double> EmptySnapshot
        = new Dictionary<string, double>(0);

    public IReadOnlyDictionary<string, double> Snapshot() => EmptySnapshot;
    public void Reset() { /* nothing to clear */ }
}
