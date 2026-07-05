using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Phase-1 signal raised by <see cref="SessionStore"/> when an aggregate
///     has met its eviction criteria (TTL expiry or pressure floor). The
///     aggregate is not yet removed from the store — subscribers get a bounded
///     deadline to persist an echo, compute derived data (Markov vector), or
///     do any other finalization work that depends on the full signal
///     inventory.
/// </summary>
/// <remarks>
///     <para>
///         Subscribers acknowledge by raising
///         <see cref="SessionFinalizedAckSignal"/> keyed by the same
///         <see cref="FingerprintId"/>. The store waits for the configured
///         expected ack count (default: 1 — the echo atom) up to
///         <see cref="DeadlineUtc"/>; whichever comes first triggers the
///         actual removal.
///     </para>
///     <para>
///         Under load the deadline is short (see
///         <see cref="SessionStoreOptions.FinalizeDeadline"/>). Slow acks
///         mean the echo is dropped — the aggregate still leaves memory,
///         accepting a shed under back-pressure. That matches the "shaped
///         eviction" model — behavioural priority already decided this
///         aggregate was worth less than newer arrivals.
///     </para>
/// </remarks>
public sealed record SessionFinalizingSignal
{
    public static readonly SignalKey<SessionFinalizingSignal> Key = new("session.finalizing");

    /// <summary>Fingerprint being finalized (matches SessionAggregate.FingerprintId).</summary>
    public required string FingerprintId { get; init; }

    /// <summary>Site partition the aggregate lived in.</summary>
    public required string SiteId { get; init; }

    /// <summary>
    ///     Frozen snapshot of the aggregate at the moment eviction was
    ///     decided. Subscribers own the reference; the store guarantees no
    ///     further mutation once this signal is raised.
    /// </summary>
    public required SessionAggregate Aggregate { get; init; }

    /// <summary>
    ///     When the store stops waiting for acks and evicts anyway. Subscribers
    ///     honouring the deadline avoid holding the aggregate in memory past
    ///     the point the store has already accounted for it as gone.
    /// </summary>
    public required DateTimeOffset DeadlineUtc { get; init; }

    /// <summary>
    ///     Reason the store elected to finalize. Diagnostic — subscribers may
    ///     want to log or shed by reason (e.g. under pressure, drop non-honeypot).
    /// </summary>
    public required SessionFinalizeReason Reason { get; init; }
}

/// <summary>
///     Phase-2 acknowledgement raised by any subscriber that has completed
///     its finalization work for the referenced session. The store counts
///     acks per fingerprint until either the configured expected count is
///     reached or the deadline expires.
/// </summary>
public sealed record SessionFinalizedAckSignal
{
    public static readonly SignalKey<SessionFinalizedAckSignal> Key = new("session.finalized.ack");

    /// <summary>Fingerprint whose finalization completed. Matches the finalizing signal.</summary>
    public required string FingerprintId { get; init; }

    /// <summary>Site partition. Included so a stale ack from a different site can't cross-match.</summary>
    public required string SiteId { get; init; }

    /// <summary>
    ///     Human-readable name of the atom that produced the ack. Diagnostic;
    ///     lets operators see which atoms are keeping up under load.
    /// </summary>
    public required string AckedBy { get; init; }
}

/// <summary>Why <see cref="SessionStore"/> elected to finalize an aggregate.</summary>
public enum SessionFinalizeReason
{
    /// <summary>Age-out: aggregate was quiet for longer than the effective TTL.</summary>
    Ttl,

    /// <summary>Absolute lifetime cap: aggregate exceeded the max session length regardless of activity.</summary>
    MaxLifetime,

    /// <summary>Pressure eviction: partition was over capacity; this aggregate had the lowest retention priority.</summary>
    Pressure,

    /// <summary>Explicit: an external caller forced eviction (BDF rig, admin API, tests).</summary>
    Explicit,
}