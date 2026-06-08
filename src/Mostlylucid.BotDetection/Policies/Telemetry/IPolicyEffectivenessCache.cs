using Mostlylucid.BotDetection.Policies.Decisions;

namespace Mostlylucid.BotDetection.Policies.Telemetry;

/// <summary>
///     Hot-path read cache for windowed policy-decision aggregates. Feeds the
///     compact-row signals on the SbPolicyStack control (trigger count,
///     matched-vs-total, action win distribution, p50/p99 evaluation
///     latency). Reads must complete in well under 100us so the dashboard
///     can render hundreds of rows per repaint without going to SQLite.
/// </summary>
/// <remarks>
///     <para>
///         Writes follow the universal write-behind LFU façade pattern: the
///         hot path appends into a per-rule in-memory ring AND enqueues into
///         a bounded channel; a background drainer batches the channel into
///         the durable <see cref="IPolicyDecisionLog"/>. The ring is truth
///         for the recent window; the durable log is truth for everything
///         older and for cold rules that were LFU-evicted.
///     </para>
///     <para>
///         Implementations MUST be safe under concurrent <see cref="OnDecisionAsync"/>
///         from arbitrary gateway threads while simultaneously serving reads.
///     </para>
/// </remarks>
public interface IPolicyEffectivenessCache
{
    /// <summary>
    ///     Called by the evaluator after each <see cref="PolicyDecision"/>.
    ///     Returns synchronously -- the write enters the bounded channel and
    ///     is drained to the log in the background. Read paths see the new
    ///     decision immediately via the ring buffer.
    /// </summary>
    ValueTask OnDecisionAsync(PolicyDecision decision, CancellationToken ct = default);

    /// <summary>
    ///     Sub-100us hot read: computes the aggregate over the requested
    ///     window from the in-memory ring. <paramref name="window"/> must be
    ///     one of <see cref="PolicyEffectivenessOptions.WindowsToTrack"/> OR
    ///     a subset thereof (it's a sliding cutoff over the same buffer).
    /// </summary>
    ValueTask<PolicyDecisionAggregate> GetAsync(Guid ruleId, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    ///     Bulk-read aggregates for many rules in a single hop. Used by the
    ///     dashboard's Effective tab to fill every visible row's trigger row
    ///     in one read.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, PolicyDecisionAggregate>> GetManyAsync(
        IReadOnlyCollection<Guid> ruleIds,
        TimeSpan window,
        CancellationToken ct = default);

    /// <summary>Hosted-service lifecycle: start the background drainer.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    ///     Hosted-service lifecycle: stop the drainer, flushing pending writes
    ///     through the durable log.
    /// </summary>
    Task StopAsync(CancellationToken ct);
}
