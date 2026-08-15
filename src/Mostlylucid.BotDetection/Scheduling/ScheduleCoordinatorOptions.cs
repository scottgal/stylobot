namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     Configuration surface for <see cref="ScheduleCoordinator"/>.
///     <para>
///         Every cadence enable/disable, threshold, and pipelining knob is on
///         this options class -- per the project memory rule that magic numbers
///         do not live in code.
///     </para>
/// </summary>
public sealed class ScheduleCoordinatorOptions
{
    /// <summary>Configuration section name when binding from <c>IConfiguration</c>.</summary>
    public const string SectionName = "BotDetection:Scheduling";

    /// <summary><c>true</c> to fire <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick1s"/>; disable on cost-sensitive hosts.</summary>
    public bool EnableTick1s { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick10s"/>.</summary>
    public bool EnableTick10s { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick1m"/>.</summary>
    public bool EnableTick1m { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick5m"/>.</summary>
    public bool EnableTick5m { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="Mostlylucid.Common.Scheduling.TickCadence.Tick1h"/>.</summary>
    public bool EnableTick1h { get; set; } = true;

    /// <summary>
    ///     A subscriber that's slow on tick N is skipped on tick N+1 (single-flight).
    ///     Every Nth skip emits a Warning so an operator can see a stuck subscriber.
    ///     Default 5: at 1s cadence that's a warning every ~5s of straight back-pressure.
    /// </summary>
    public int OverlapWarnEveryNth { get; set; } = 5;

    /// <summary>
    ///     Reserved for resource-aware pipelining; currently unused (subscribers
    ///     all fire in parallel via <see cref="System.Threading.Tasks.Task.Run(System.Action)"/>).
    ///     Wire when the gateway sees real CPU pressure from concurrent high-cost
    ///     subscribers on the same tick. Default <see cref="int.MaxValue"/> (no cap).
    /// </summary>
    public int MaxConcurrentSubscribersPerTick { get; set; } = int.MaxValue;

    // ---- Cadence loop fault tolerance --------------------------------------
    //
    // The cadence loop self-heals: any non-shutdown fault in the loop body is
    // logged loudly and re-entered at the next wall-clock boundary instead of
    // exiting forever (the tick-death doctrine, operator directive 2026-08-15).
    // These knobs bound the re-entry backoff.

    /// <summary>
    ///     Base backoff between cadence-loop re-entries after a fault. The
    ///     actual wait is <c>LoopFaultBackoff * consecutiveFaults</c>, capped at
    ///     <see cref="LoopFaultMaxBackoff"/>. The consecutive-fault streak resets
    ///     on the first successful tick after a fault. Default 5s -- long
    ///     enough to avoid a hot re-entry spin on a deterministic fault, short
    ///     enough that a transient fault costs at most a few ticks.
    /// </summary>
    public TimeSpan LoopFaultBackoff { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Ceiling for the cadence-loop fault backoff. Default 30s -- at that
    ///     point the loop is still firing log lines every 30s (loud, never
    ///     silent) while the watchdog remains the last-resort process exit.
    /// </summary>
    public TimeSpan LoopFaultMaxBackoff { get; set; } = TimeSpan.FromSeconds(30);

    // ---- Watchdog -----------------------------------------------------------
    //
    // ScheduleCoordinatorWatchdog is the irreducible bootstrap BackgroundService
    // that observes per-cadence freshness and forces a process exit when any
    // cadence loop goes silent. With 50+ subscribers depending on the
    // coordinator, a silent cadence-loop crash means mass detection blindness
    // with no in-process recovery; the watchdog ensures K8s / systemd restarts
    // the process. See ScheduleCoordinatorWatchdog for the rationale.

    /// <summary>
    ///     <c>true</c> to enable <c>ScheduleCoordinatorWatchdog</c>. Default
    ///     <c>true</c> in production; tests may turn it off to avoid spurious
    ///     <c>StopApplication</c> calls when the coordinator is never started.
    /// </summary>
    public bool EnableWatchdog { get; set; } = true;

    /// <summary>
    ///     Multiplier applied to each cadence's period to decide "this cadence
    ///     has gone silent". A cadence is considered silent if its last tick is
    ///     older than <c>period * StalenessMultiplier</c>. Default 2.0 -- one
    ///     missed tick is forgivable (GC pause, momentary CPU starve); two is
    ///     a stuck loop.
    /// </summary>
    public double WatchdogStalenessMultiplier { get; set; } = 2.0;

    /// <summary>
    ///     How often the watchdog evaluates per-cadence freshness. Default 30s.
    ///     Lower than 30s wastes CPU; higher than 60s extends mean time-to-
    ///     detect on a silent failure beyond a typical operator-attention
    ///     threshold.
    /// </summary>
    public TimeSpan WatchdogCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Grace period applied at process start before the watchdog will fire.
    ///     Cadence loops need an initial wall-clock alignment delay (up to one
    ///     period) before their first tick; firing the watchdog during that
    ///     alignment window would be a false positive. Default 2 minutes
    ///     covers the slowest cadence the watchdog observes (<c>tick.1m</c>
    ///     with a 2x staleness multiplier).
    /// </summary>
    public TimeSpan WatchdogStartupGrace { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Fault-storm detection: how many additional subscriber faults
    ///     (between two consecutive watchdog checks) mark a subscriber as
    ///     storming. A subscriber that throws on EVERY tick makes its work
    ///     silently dead while the cadence itself looks healthy -- the
    ///     watchdog's silence check cannot see it. When a watched cadence's
    ///     subscriber crosses this delta, the watchdog forces the same
    ///     supervisor-restart path the silence check uses. Default 6 faults
    ///     per 30s check interval; a value &lt;= 0 disables the check.
    /// </summary>
    public int WatchdogFaultStormThreshold { get; set; } = 6;
}