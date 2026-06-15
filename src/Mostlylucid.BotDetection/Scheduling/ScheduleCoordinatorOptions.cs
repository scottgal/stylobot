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

    /// <summary><c>true</c> to fire <see cref="TickCadence.Tick1s"/>; disable on cost-sensitive hosts.</summary>
    public bool EnableTick1s { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="TickCadence.Tick10s"/>.</summary>
    public bool EnableTick10s { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="TickCadence.Tick1m"/>.</summary>
    public bool EnableTick1m { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="TickCadence.Tick5m"/>.</summary>
    public bool EnableTick5m { get; set; } = true;

    /// <summary><c>true</c> to fire <see cref="TickCadence.Tick1h"/>.</summary>
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
}