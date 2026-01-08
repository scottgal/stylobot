using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Base class for analysis lanes with common signal emission logic.
/// </summary>
public abstract class AnalysisLaneBase : IAnalysisLane
{
    protected readonly SignalSink Sink;

    protected AnalysisLaneBase(SignalSink sink)
    {
        Sink = sink;
    }

    public abstract string Name { get; }

    public abstract Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Emit a score signal for this lane.
    /// </summary>
    protected void EmitScore(double score)
    {
        Sink.Raise($"{Name}.score", score.ToString("F4"));
    }

    /// <summary>
    ///     Emit a metric signal for this lane.
    /// </summary>
    protected void EmitMetric(string metricName, string value)
    {
        Sink.Raise($"{Name}.{metricName}", value);
    }
}