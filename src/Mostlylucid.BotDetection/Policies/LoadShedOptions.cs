using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy load-shed configuration. Consulted by
///     <see cref="Services.LoadShedDecision"/> at request intake, BEFORE the
///     orchestrator is called. Sheds (refuses with 503 + Retry-After) when the
///     pipeline is under sustained High or Critical load, as reported by
///     <see cref="Services.PipelineLoadSensor.CurrentBand"/>.
///     <para>
///     Visitor-class-aware: every request is resolved against
///     <see cref="HumanGate"/> and <see cref="BotGate"/> to produce a
///     <see cref="VisitorClass"/>; the resolved class picks the matching
///     shed fraction. Humans are never shed by default, bots always shed
///     when the band escalates, and unknowns shed at an operator-tunable
///     fraction. An operator who wants humans shed under critical pressure
///     must explicitly set <see cref="HumanShedAtCritical"/> &gt; 0.
///     </para>
///     <para>
///     Normal and Low bands never shed any class; the per-class fractions
///     are only consulted at High and Critical. Sensor designs that expose
///     additional bands (currently none) should extend this options shape.
///     </para>
/// </summary>
public sealed record LoadShedOptions
{
    /// <summary>
    ///     Legacy per-band shed fraction for the High band. Kept for
    ///     backward-compat with operator configs that pre-date the
    ///     visitor-class-aware redesign. The runtime now reads
    ///     <see cref="UnknownShedAtHigh"/> instead; this field has no
    ///     effect on the shed decision.
    /// </summary>
    public double DropFractionAtHigh { get; init; } = 0.2;

    /// <summary>
    ///     Legacy per-band shed fraction for the Critical band. Kept for
    ///     backward-compat with operator configs that pre-date the
    ///     visitor-class-aware redesign. The runtime now reads
    ///     <see cref="UnknownShedAtCritical"/> instead; this field has no
    ///     effect on the shed decision.
    /// </summary>
    public double DropFractionAtCritical { get; init; } = 0.5;

    /// <summary>
    ///     Boundary defining which cached (prob, conf) tuples count as human
    ///     for the never-shed-humans-by-default guarantee. Default:
    ///     prob &lt;= 0.3 AND conf &gt;= 0.7.
    /// </summary>
    public ClassGate HumanGate { get; init; } = new(MaxBotProb: 0.3, MinConfidence: 0.7);

    /// <summary>
    ///     Boundary defining which cached (prob, conf) tuples count as bot
    ///     for the shed-bots-first guarantee. Default:
    ///     prob &gt;= 0.5 AND conf &gt;= 0.7.
    /// </summary>
    public ClassGate BotGate { get; init; } = new(MinBotProb: 0.5, MinConfidence: 0.7);

    /// <summary>Fraction of human-class requests shed at High band. Default 0.0 (never).</summary>
    public double HumanShedAtHigh { get; init; } = 0.0;

    /// <summary>Fraction of human-class requests shed at Critical band. Default 0.0 (never).</summary>
    public double HumanShedAtCritical { get; init; } = 0.0;

    /// <summary>Fraction of unknown-class requests shed at High band. Default 0.3.</summary>
    public double UnknownShedAtHigh { get; init; } = 0.3;

    /// <summary>Fraction of unknown-class requests shed at Critical band. Default 0.7.</summary>
    public double UnknownShedAtCritical { get; init; } = 0.7;

    /// <summary>Fraction of bot-class requests shed at High band. Default 1.0 (always).</summary>
    public double BotShedAtHigh { get; init; } = 1.0;

    /// <summary>Fraction of bot-class requests shed at Critical band. Default 1.0 (always).</summary>
    public double BotShedAtCritical { get; init; } = 1.0;
}
