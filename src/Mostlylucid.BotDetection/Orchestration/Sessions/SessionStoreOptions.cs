namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Sizing + retention knobs for <see cref="SessionStore"/>. The store
///     holds per-fingerprint <see cref="SessionAggregate"/> entries in a
///     shared per-domain surface with shaped eviction under pressure.
/// </summary>
/// <remarks>
///     <para>Bound from <c>BotDetection:Session</c>.</para>
///     <para>
///         Auto-derives entry cap from available memory the same way
///         <c>SessionSignatureResponseCoordinatorCacheOptions</c> did --
///         operators typically leave the caps unset and let the box size
///         itself.
///     </para>
/// </remarks>
public sealed class SessionStoreOptions
{
    public const string SectionName = "BotDetection:Session";

    /// <summary>
    ///     Init signal raised on <see cref="StyloFlow.Orchestration.IInitSignalBus"/>
    ///     the first time an escalator upserts into this store. Consumers
    ///     (<see cref="SessionAtom"/>, <see cref="SessionPersistenceAtom"/>)
    ///     lazy-boot via <c>AddOnInitSignal&lt;T&gt;</c> against this name.
    ///     Kept as a public constant so the DI wire-up, atoms, and StyloFlow
    ///     manifests all reference the same identifier.
    /// </summary>
    public const string InitSignal = "init.session";

    /// <summary>
    ///     Explicit per-domain cap. When null (default), auto-derives via
    ///     <see cref="MemoryBudgetPercent"/> + <see cref="BytesPerEntryEstimate"/>
    ///     clamped to [<see cref="MinAutoMaxAggregates"/>, <see cref="MaxAutoMaxAggregates"/>].
    ///     Applies per-site so a busy tenant does not evict a quieter tenant.
    /// </summary>
    public int? MaxAggregatesPerSite { get; set; }

    /// <summary>Auto-derivation memory budget as a percent of available memory.</summary>
    public double MemoryBudgetPercent { get; set; } = 5.0;

    /// <summary>
    ///     Per-<see cref="SessionAggregate"/> memory estimate. Aggregates
    ///     are small (a few scalars + a small dictionary), so this is much
    ///     lower than the old coordinator-per-session estimate.
    /// </summary>
    public int BytesPerEntryEstimate { get; set; } = 1_024;

    /// <summary>Lower clamp for the auto-derived cap.</summary>
    public int MinAutoMaxAggregates { get; set; } = 500;

    /// <summary>Upper clamp for the auto-derived cap.</summary>
    public int MaxAutoMaxAggregates { get; set; } = 100_000;

    /// <summary>
    ///     Sliding TTL applied to aggregates. Access via <c>Upsert</c>
    ///     resets it. Under pressure the cleanup loop shrinks the effective
    ///     TTL toward <see cref="MinTtlUnderPressure"/> so old aggregates
    ///     drop faster.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Absolute upper bound on aggregate lifetime regardless of access.
    ///     Same shape as <c>SlidingCacheAtom</c>'s absolute expiration.
    /// </summary>
    public TimeSpan MaxLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    ///     Floor for adaptive TTL when the store is at capacity. The
    ///     shaped-eviction curve interpolates <see cref="Ttl"/> down toward
    ///     this floor as headroom drops. Never below this so we do not
    ///     starve genuine in-flight sessions.
    /// </summary>
    public TimeSpan MinTtlUnderPressure { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cleanup loop cadence. Runs the retention pass + adaptive TTL update.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Two-phase finalize: how long the store waits after raising
    ///     <see cref="SessionFinalizingSignal"/> for the configured number
    ///     of <see cref="SessionFinalizedAckSignal"/> acks before evicting
    ///     the aggregate anyway. Bounded so slow subscribers cannot pin
    ///     memory indefinitely. Under pressure the store may shrink this
    ///     further via the same adaptive curve applied to
    ///     <see cref="Ttl"/> — set the floor via
    ///     <see cref="MinFinalizeDeadlineUnderPressure"/>.
    /// </summary>
    public TimeSpan FinalizeDeadline { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Floor for the adaptive finalize deadline under pressure.</summary>
    public TimeSpan MinFinalizeDeadlineUnderPressure { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     How many <see cref="SessionFinalizedAckSignal"/> acks the store
    ///     expects before considering finalization complete. Default 1 —
    ///     the echo atom is the sole persistence subscriber. Raise to N
    ///     if additional atoms (Markov enrichment, external audit sink,
    ///     control-plane replicator) also need to ack before eviction.
    /// </summary>
    public int ExpectedAckCount { get; set; } = 1;

    /// <summary>
    ///     Emit the finalizing signal at all. Off in tests / trimmed hosts
    ///     that do not wire an echo atom (avoids the deadline wait when
    ///     nothing is listening). Default true — production always has an
    ///     echo atom wired via <see cref="AddOnInitSignal"/>.
    /// </summary>
    public bool EmitFinalizingSignal { get; set; } = true;

    /// <summary>
    ///     How often the cleanup loop re-checks whether every pending
    ///     finalization latch has completed while waiting for acks. Lower
    ///     = faster response when subscribers ack quickly; higher = less
    ///     CPU / fewer wake-ups per pass. Bounded well below
    ///     <see cref="MinFinalizeDeadlineUnderPressure"/> so under-pressure
    ///     evictions do not stall on the poll cadence.
    /// </summary>
    public TimeSpan AckPollInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    ///     Resolves the effective per-site aggregate cap, auto-deriving
    ///     from available memory when <see cref="MaxAggregatesPerSite"/> is
    ///     unset. Mirrors the resolver on the retired
    ///     <c>SessionSignatureResponseCoordinatorCacheOptions</c>.
    /// </summary>
    public int ResolveMaxAggregatesPerSite()
    {
        if (MaxAggregatesPerSite.HasValue && MaxAggregatesPerSite.Value > 0)
            return MaxAggregatesPerSite.Value;

        long available = 0;
        try { available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { available = 512L * 1024 * 1024; }

        if (available <= 0) available = 512L * 1024 * 1024;
        var derived = (long)(available * MemoryBudgetPercent / 100.0 / BytesPerEntryEstimate);
        return (int)Math.Clamp(derived, MinAutoMaxAggregates, MaxAutoMaxAggregates);
    }
}