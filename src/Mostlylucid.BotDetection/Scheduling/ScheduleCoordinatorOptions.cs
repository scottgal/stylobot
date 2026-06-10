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
}