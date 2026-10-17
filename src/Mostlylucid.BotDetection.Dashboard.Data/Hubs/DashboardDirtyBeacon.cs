namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     Structured beacon emitted once per <see cref="SignalRBroadcastConstrainer"/>
///     flush window (Plan 3 Task 2). Carries the monotonic tick at which the flush
///     fired and the set of unique surface names that changed during the window.
///     <para>
///         Consumers subscribe to
///         <see cref="IStyloBotDashboardHub.BroadcastDirty"/> and compare the
///         beacon's <see cref="Tick"/> against each widget's <c>data-sb-tick</c>
///         attribute to skip widgets that are already current.
///     </para>
///     <para>
///         <b>Minimal by design.</b> No priority, no metadata, no per-surface
///         deltas — just the tick and the closed set of surface names from
///         <see cref="Mostlylucid.BotDetection.UI.Services.DashboardFreshnessBeacon.Surfaces"/>.
///     </para>
/// </summary>
public sealed record DashboardDirtyBeacon(long Tick, IReadOnlyList<string> DirtyKinds);
