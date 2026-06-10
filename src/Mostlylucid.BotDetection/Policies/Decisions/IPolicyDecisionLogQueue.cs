namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Write-behind shim in front of <see cref="IPolicyDecisionLog"/>. The
///     <see cref="Policies.Dispatch.PolicyActionDispatcher"/> enqueues
///     decisions via this contract instead of awaiting
///     <see cref="IPolicyDecisionLog.AppendAsync"/> directly; the default
///     implementation drains the queue into the underlying log on the
///     project's <see cref="Scheduling.TickCadence.Tick1s"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Wave 4 architectural-drift remediation:</b> the dispatcher
///         previously awaited the durable log inline on the request hot path.
///         Even when the implementation was a fast channel-TryWrite, the
///         async state machine traversal added per-request overhead and
///         coupled request latency to whatever store backed the log. This
///         queue moves the write off the request thread entirely.
///     </para>
///     <para>
///         Reuses the same write-behind pattern as
///         <see cref="Telemetry.PolicyEffectivenessCache"/>: bounded channel
///         + drainer ticking on the project's
///         <see cref="Scheduling.IScheduleCoordinator"/>. We do NOT introduce
///         a new write-behind primitive (per the
///         <c>feedback_write_behind_lfu_facade</c> guidance).
///     </para>
/// </remarks>
public interface IPolicyDecisionLogQueue
{
    /// <summary>
    ///     Enqueue a decision for write-behind durability. The call MUST be
    ///     non-blocking; a full queue drops the oldest entry rather than
    ///     stalling the request thread.
    /// </summary>
    void Enqueue(PolicyDecision decision);

    /// <summary>
    ///     Drain every queued decision into the underlying
    ///     <see cref="IPolicyDecisionLog"/>. Called by the project's tick
    ///     coordinator at <see cref="Scheduling.TickCadence.Tick1s"/> in
    ///     production; tests call this directly via the coordinator's
    ///     <c>TickOnceAsync</c> hook to flush the queue deterministically.
    /// </summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>
    ///     Total number of <see cref="Enqueue"/> calls that dropped because
    ///     the bounded channel was full or completed. Surfaced for diagnostic
    ///     dashboards and future metric registration.
    /// </summary>
    long DroppedCount { get; }
}
