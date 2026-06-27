using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Per-coordinator-instance configuration. Each concrete caller binds its own
///     Options so subscribers can be told apart in the schedule dashboard and
///     tuned independently.
/// </summary>
public sealed class EphemeralLlmCoordinatorOptions
{
    /// <summary>Which IScheduleCoordinator cadence drives picks. Default Tick1m.</summary>
    public TickCadence Cadence { get; set; } = TickCadence.Tick1m;

    /// <summary>Max number of items the picker is asked for per tick. Default 10.</summary>
    public int MaxItemsPerTick { get; set; } = 10;

    /// <summary>Hard ceiling on parallel LLM invocations across the picked batch. Default 4.</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>Per-invocation timeout backstop. The coordinator cancels a hung
    /// invoker at this point; the caller's IEphemeralLlmInvoker should observe
    /// the same value internally too. Default 30s.</summary>
    public TimeSpan InvocationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Subscriber name used in log lines and on the schedule dashboard.
    /// Each concrete caller (FingerprintNamer, PartialInducer, etc.) sets this
    /// so the operator can tell instances apart.</summary>
    public string SubscriberName { get; set; } = "ephemeral-llm";
}
