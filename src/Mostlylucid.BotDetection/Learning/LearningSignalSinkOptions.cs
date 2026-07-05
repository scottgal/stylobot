namespace Mostlylucid.BotDetection.Learning;

/// <summary>
///     Configurable knobs for the shared learning
///     <see cref="Mostlylucid.Ephemeral.TypedSignalSink{TPayload}"/> that
///     escalators write into and the <see cref="LearningCoordinator"/> drains.
///     The sink is DI-registered as a singleton independently of the
///     coordinator so escalators can raise without a coordinator dependency
///     (and, once the StyloFlow start-signal primitive lands, so the
///     coordinator can lazy-boot on first raise).
/// </summary>
/// <remarks>
///     <para>
///         Bound from <c>BotDetection:Learning</c>.
///     </para>
///     <para>
///         <b>Sizing.</b> Capacity + retention must exceed the largest of:
///         <list type="bullet">
///             <item>
///                 Boot-latency bridge — the wall time between the first
///                 escalator raise and the coordinator becoming ready to
///                 drain (relevant once lazy boot is wired).
///             </item>
///             <item>
///                 Slowest consumer tick — persistence samplers, LFU
///                 drainers, and dashboard aggregators typically batch on
///                 tick cadences (1s / 10s / 1m). Retention must span the
///                 largest of these so no batch misses events raised in the
///                 prior interval.
///             </item>
///             <item>
///                 Persistence sampling adequacy — the durable-storage
///                 subscriber samples a fraction of raised events. If the
///                 sampler needs N events in a retention window to produce
///                 a useful corpus for the learning loops, retention *
///                 expected-throughput * sample-rate must ≥ N. This is the
///                 "store ENOUGH" constraint.
///             </item>
///         </list>
///         Defaults are sized for the typical always-on gateway; raise
///         retention when the operator adds a slower sampler or a
///         low-cadence persistence flush.
///     </para>
/// </remarks>
public sealed class LearningSignalSinkOptions
{
    public const string SectionName = "BotDetection:Learning";

    /// <summary>
    ///     Init signal raised on the shared coordination bus the first time
    ///     an escalator writes to this sink. Coordinator bootstraps subscribe
    ///     to this name to know when to lazy-construct the coordinator + its
    ///     dispatcher. Kept as a public constant so escalators, bootstraps,
    ///     and StyloFlow manifests can all reference the same identifier.
    /// </summary>
    public const string InitSignal = "init.learning";

    /// <summary>
    ///     Max <see cref="Mostlylucid.BotDetection.Events.LearningEvent"/>
    ///     entries retained on the sink before oldest-eviction kicks in.
    ///     Combine with <see cref="Retention"/> per the sizing bullets above.
    /// </summary>
    public int Capacity { get; set; } = 4096;

    /// <summary>
    ///     Sliding retention window for events on the sink. Older events are
    ///     dropped even if capacity has headroom.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(5);
}