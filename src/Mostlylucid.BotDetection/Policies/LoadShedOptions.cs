namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy load-shed configuration. Consulted by <see cref="Services.LoadShedDecision"/>
///     at request intake, BEFORE the orchestrator is called. Sheds (skips detection) for a
///     fraction of requests when the pipeline is under sustained High or Critical load,
///     as reported by <see cref="Services.PipelineLoadSensor.CurrentBand"/>.
///     Defaults are zero, so load-shedding is opt-in.
/// </summary>
public sealed record LoadShedOptions
{
    /// <summary>Fraction of requests to shed when load band is High. Range 0.0 to 1.0. Default 0.0.</summary>
    public double DropFractionAtHigh { get; init; }

    /// <summary>Fraction of requests to shed when load band is Critical. Range 0.0 to 1.0. Default 0.0.</summary>
    public double DropFractionAtCritical { get; init; }
}
