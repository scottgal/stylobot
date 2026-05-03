using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Base class for analysis lanes with common signal emission logic.
///     A coordinator key prefix is embedded in every signal name so that multiple coordinators
///     sharing the same SignalSink cannot read each other's scores.
/// </summary>
public abstract class AnalysisLaneBase : IAnalysisLane
{
    /// <summary>
    ///     Per-coordinator prefix injected at construction time (e.g. "c.{signature}").
    ///     All emitted signals are namespaced under this prefix so that a shared SignalSink
    ///     never serves one coordinator's scores to a different coordinator.
    /// </summary>
    private readonly string _coordinatorKey;

    protected readonly SignalSink Sink;

    protected AnalysisLaneBase(SignalSink sink, string coordinatorKey)
    {
        Sink = sink;
        _coordinatorKey = coordinatorKey;
    }

    public abstract string Name { get; }

    /// <summary>
    ///     The fully-qualified signal name for this lane's score, scoped to the owning coordinator.
    ///     Use this when reading the score back out of the shared sink.
    /// </summary>
    public string ScopedScoreKey => $"{_coordinatorKey}.{Name}.score";

    public abstract Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Emit a score signal for this lane, scoped to the owning coordinator.
    /// </summary>
    protected void EmitScore(double score)
    {
        Sink.Raise(ScopedScoreKey, score.ToString("F4"));
    }

    /// <summary>
    ///     Emit a metric signal for this lane, scoped to the owning coordinator.
    /// </summary>
    protected void EmitMetric(string metricName, string value)
    {
        Sink.Raise($"{_coordinatorKey}.{Name}.{metricName}", value);
    }
}