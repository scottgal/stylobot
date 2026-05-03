using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Detectors;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     A detector that reacts to signals from other detectors.
///     Declares what signals it produces and what signals it requires.
/// </summary>
public interface IReactiveDetector : IDetector
{
    /// <summary>
    ///     Signal keys this detector produces
    /// </summary>
    IReadOnlySet<string> ProducesSignals { get; }

    /// <summary>
    ///     Signal keys this detector requires before it can run.
    ///     Empty set means the detector has no dependencies (can run immediately).
    /// </summary>
    IReadOnlySet<string> RequiresSignals => new HashSet<string>();

    /// <summary>
    ///     Maximum time to wait for required signals before running anyway
    /// </summary>
    TimeSpan SignalTimeout => TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Whether this detector can run with partial signals (some missing)
    /// </summary>
    bool CanRunWithPartialSignals => false;

    /// <summary>
    ///     Run detection with access to the signal bus.
    ///     Detector should:
    ///     1. Read required signals from the bus
    ///     2. Perform detection
    ///     3. Publish its own signals to the bus
    /// </summary>
    Task<DetectorResult> DetectAsync(
        HttpContext context,
        IDetectionSignalBus signalBus,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Base class for reactive detectors with common functionality
/// </summary>
public abstract class ReactiveDetectorBase : IReactiveDetector
{
    public abstract string Name { get; }
    public abstract IReadOnlySet<string> ProducesSignals { get; }
    public virtual IReadOnlySet<string> RequiresSignals => new HashSet<string>();
    public virtual TimeSpan SignalTimeout => TimeSpan.FromMilliseconds(500);
    public virtual bool CanRunWithPartialSignals => false;

    // Default stage based on whether we have dependencies
    public DetectorStage Stage => RequiresSignals.Count == 0
        ? DetectorStage.RawSignals
        : DetectorStage.MetaAnalysis;

    /// <summary>
    ///     Legacy method - calls the reactive version with a new signal bus
    /// </summary>
    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var signalBus = new DetectionSignalBus();
        return await DetectAsync(context, signalBus, cancellationToken);
    }

    /// <summary>
    ///     New reactive method with signal bus
    /// </summary>
    public abstract Task<DetectorResult> DetectAsync(
        HttpContext context,
        IDetectionSignalBus signalBus,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Helper to publish a signal
    /// </summary>
    protected void PublishSignal(IDetectionSignalBus bus, string key, object value, double confidence = 1.0)
    {
        bus.Publish(new DetectionSignal
        {
            Key = key,
            Value = value,
            SourceDetector = Name,
            Confidence = confidence
        });
    }

    /// <summary>
    ///     Helper to get a required signal value
    /// </summary>
    protected T? GetSignal<T>(IReadOnlyDictionary<string, DetectionSignal> signals, string key)
    {
        return signals.TryGetValue(key, out var signal) ? signal.GetValue<T>() : default;
    }
}

/// <summary>
///     Detector priority for ordering when multiple detectors can run
/// </summary>
public enum DetectorPriority
{
    /// <summary>Critical detectors run first (e.g., whitelist checks)</summary>
    Critical = 0,

    /// <summary>High priority for fast, high-signal detectors</summary>
    High = 1,

    /// <summary>Normal priority</summary>
    Normal = 2,

    /// <summary>Low priority for slow or expensive detectors</summary>
    Low = 3,

    /// <summary>Lowest priority for learning/feedback (doesn't affect result)</summary>
    Learning = 4
}