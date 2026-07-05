namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Configurable knobs for the shared LLM-classification request sink
///     that fronts the <see cref="LlmClassificationCoordinator"/>. Escalators
///     raise onto this sink; the coordinator subscribes at construction
///     (lazy-boot via <c>AddOnInitSignal&lt;LlmClassificationCoordinator&gt;</c>)
///     and forwards each raise into its internal bounded-channel drain so
///     the expensive LLM call cadence stays under the coordinator's control.
/// </summary>
/// <remarks>
///     <para>Bound from <c>BotDetection:LlmSink</c>.</para>
///     <para>
///         The sink is intentionally the fan-in point only. Throttling,
///         concurrency capping, and sequential-per-tick draining stay
///         inside the coordinator because LLM calls take seconds and
///         uncontrolled fire-and-forget would blow the provider's rate
///         limits.
///     </para>
/// </remarks>
public sealed class LlmClassificationSinkOptions
{
    public const string SectionName = "BotDetection:LlmSink";

    /// <summary>
    ///     Init signal fired on the shared bus the first time an escalator
    ///     raises onto the sink. The coordinator's registration wires via
    ///     <c>AddOnInitSignal&lt;LlmClassificationCoordinator&gt;</c> so
    ///     nothing about the LLM pipeline is constructed until an actual
    ///     escalation lands.
    /// </summary>
    public const string InitSignal = "init.llm";

    /// <summary>
    ///     Retention window on the sink. Sized to cover the LLM
    ///     coordinator's boot latency plus one tick of the internal drain
    ///     cadence so no in-flight raise is lost between init-fire and
    ///     subscription. Independent of the internal channel capacity.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Sink capacity (bounded raise history). The coordinator drains
    ///     through its own bounded <c>Channel&lt;T&gt;</c>; the sink itself
    ///     only needs enough headroom to absorb bursts between fan-in and
    ///     the channel's TryWrite. Kept modest to avoid double-buffering
    ///     the same request queue.
    /// </summary>
    public int Capacity { get; set; } = 512;
}