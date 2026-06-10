namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     Project-wide tick cadences emitted by <see cref="IScheduleCoordinator"/>.
///     The five values are the only cadences the FOSS project commits to; if a
///     subscriber needs something else (3s, 30s, 15m) it should aggregate one of
///     these inside the handler rather than asking the coordinator for a new
///     cadence.
/// </summary>
public enum TickCadence
{
    /// <summary>~1Hz oscilloscope tick (top of every wall-clock second).</summary>
    Tick1s,

    /// <summary>10s rollup tick (each multiple of 10s past the minute).</summary>
    Tick10s,

    /// <summary>1m bookkeeping tick (top of every wall-clock minute).</summary>
    Tick1m,

    /// <summary>5m housekeeping tick (each multiple of 5min past the hour).</summary>
    Tick5m,

    /// <summary>1h archival tick (top of every wall-clock hour).</summary>
    Tick1h
}

/// <summary>
///     Operator hint for how expensive a subscriber's handler is. Used by the
///     resource-aware pipelining surface (currently advisory only; the
///     coordinator honours <see cref="ScheduleCoordinatorOptions.MaxConcurrentSubscribersPerTick"/>
///     when set).
/// </summary>
public enum CostHint
{
    /// <summary>O(1) bookkeeping; safe to fire on every tick alongside any number of siblings.</summary>
    Low,

    /// <summary>Mid-cost work (a small SQL pass, a bounded LRU scan).</summary>
    Medium,

    /// <summary>Heavy work (full DB sweep, large recompute) -- pipeline-stagger-able.</summary>
    High
}

/// <summary>
///     The single source of truth for time-based ticks across the process.
///     <para>
///         <see cref="ScheduleCoordinator"/> emits stable wall-clock-aligned
///         ticks at the five cadences in <see cref="TickCadence"/> and lets units
///         subscribe to the cadence they care about. Replaces the ~39 ad-hoc
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> timer
///         loops the project had accumulated.
///     </para>
///     <para>
///         Subscribers depend on this interface as nullable: a viewer host that
///         never called <c>AddBotDetection</c> won't have a coordinator
///         registered, and subscribers fail closed at boot (see the
///         <c>feedback_remote_mode_optional_di</c> memory).
///     </para>
/// </summary>
public interface IScheduleCoordinator
{
    /// <summary>
    ///     Subscribe a handler to a tick cadence. Handler invocation is guaranteed:
    ///     <list type="bullet">
    ///         <item>
    ///             single-flight per subscription -- no re-entry while the previous
    ///             invocation is still running (overlap is observed via
    ///             <see cref="TickSubscriberMetadata.OverlapSkipCount"/>);
    ///         </item>
    ///         <item>
    ///             fault-isolated -- a throwing handler does NOT prevent other
    ///             subscribers running on the same tick, and the fault is counted
    ///             on the subscriber's metadata;
    ///         </item>
    ///         <item>
    ///             cancelled on coordinator shutdown via the
    ///             <see cref="System.Threading.CancellationToken"/> passed to the
    ///             handler.
    ///         </item>
    ///     </list>
    /// </summary>
    /// <param name="cadence">Which <see cref="TickCadence"/> the handler subscribes to.</param>
    /// <param name="subscriberName">Used in log lines and the overlap warning; should identify the calling class or feature.</param>
    /// <param name="costHint">Operator hint for resource-aware pipelining.</param>
    /// <param name="handler">
    ///     Async callback. Receives the tick's wall-clock timestamp and a
    ///     <see cref="System.Threading.CancellationToken"/> that fires on coordinator shutdown.
    /// </param>
    /// <returns>A disposable handle. Disposing unsubscribes the handler.</returns>
    IDisposable Subscribe(
        TickCadence cadence,
        string subscriberName,
        CostHint costHint,
        Func<DateTimeOffset, CancellationToken, Task> handler);

    /// <summary>
    ///     Snapshot of current subscriber metadata for diagnostics dashboards.
    ///     The list is a point-in-time copy; metadata mutates as subscribers fire.
    /// </summary>
    IReadOnlyList<TickSubscriberMetadata> Snapshot();
}

/// <summary>
///     Diagnostic snapshot of a single subscription -- one record per active
///     <see cref="IScheduleCoordinator.Subscribe"/> call.
/// </summary>
/// <param name="Cadence">The cadence this subscriber is registered for.</param>
/// <param name="SubscriberName">The name the subscription registered with.</param>
/// <param name="Hint">The cost hint the subscription registered with.</param>
/// <param name="LastInvokedAt">Wall-clock instant of the most recent invocation; <c>null</c> if never fired.</param>
/// <param name="LastDuration">Wall-clock duration of the most recent invocation; <c>null</c> if never fired.</param>
/// <param name="OverlapSkipCount">Cumulative number of ticks skipped because the previous invocation was still running.</param>
/// <param name="FaultCount">Cumulative number of times the handler threw.</param>
public sealed record TickSubscriberMetadata(
    TickCadence Cadence,
    string SubscriberName,
    CostHint Hint,
    DateTimeOffset? LastInvokedAt,
    TimeSpan? LastDuration,
    int OverlapSkipCount,
    int FaultCount);