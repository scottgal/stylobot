using Mostlylucid.BotDetection.Orchestration.Signals;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Analysis lane that processes signature behavior patterns.
///     Each lane analyzes a specific aspect (behavioral, spectral, reputation, etc.)
/// </summary>
public interface IAnalysisLane
{
    /// <summary>Lane name for identification and signal keys</summary>
    string Name { get; }

    /// <summary>
    ///     Analyze a window of operations and emit results to sink.
    /// </summary>
    Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window, CancellationToken cancellationToken = default);
}