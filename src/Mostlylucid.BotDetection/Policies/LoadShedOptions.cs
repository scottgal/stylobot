namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy load-shed configuration. Consulted by <see cref="Services.LoadShedDecision"/>
///     at request intake, BEFORE the orchestrator is called. Sheds (skips detection) for a
///     fraction of requests when the pipeline is under sustained High or Critical load,
///     as reported by <see cref="Services.PipelineLoadSensor.CurrentBand"/>.
///     <para>
///     Self-protection is opt-OUT, not opt-in: defaults shed 20% at High and 50% at
///     Critical so every policy stays responsive under overload unless an operator
///     explicitly disables shedding (e.g. <c>DropFractionAtHigh = 0.0</c>). A policy
///     that never sheds is a policy that queues itself to death once the sensor reads
///     Critical -- the wrong default for a security product.
///     </para>
/// </summary>
public sealed record LoadShedOptions
{
    /// <summary>Fraction of requests to shed when load band is High. Range 0.0 to 1.0. Default 0.2.</summary>
    public double DropFractionAtHigh { get; init; } = 0.2;

    /// <summary>Fraction of requests to shed when load band is Critical. Range 0.0 to 1.0. Default 0.5.</summary>
    public double DropFractionAtCritical { get; init; } = 0.5;
}
