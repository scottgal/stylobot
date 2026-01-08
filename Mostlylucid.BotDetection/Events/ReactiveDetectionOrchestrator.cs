using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Detectors;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Orchestrates detection using an event-driven signal bus.
///     Detectors declare dependencies and run when their required signals arrive.
/// </summary>
public class ReactiveDetectionOrchestrator
{
    private readonly IEnumerable<IDetector> _detectors;
    private readonly ILogger<ReactiveDetectionOrchestrator> _logger;
    private readonly TimeSpan _overallTimeout;

    public ReactiveDetectionOrchestrator(
        IEnumerable<IDetector> detectors,
        ILogger<ReactiveDetectionOrchestrator> logger,
        TimeSpan? overallTimeout = null)
    {
        _detectors = detectors;
        _logger = logger;
        _overallTimeout = overallTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    ///     Run all detectors in signal-driven order
    /// </summary>
    public async Task<DetectionPipelineResult> RunAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var signalBus = new DetectionSignalBus();
        var results = new Dictionary<string, DetectorResult>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_overallTimeout);

        try
        {
            // Separate reactive and legacy detectors
            var reactiveDetectors = _detectors.OfType<IReactiveDetector>().ToList();
            var legacyDetectors = _detectors.Where(d => d is not IReactiveDetector).ToList();

            // Group reactive detectors by dependency level
            var noDeps = reactiveDetectors.Where(d => !d.RequiresSignals.Any()).ToList();
            var withDeps = reactiveDetectors.Where(d => d.RequiresSignals.Any()).ToList();

            _logger.LogDebug(
                "Starting reactive detection: {NoDeps} independent, {WithDeps} dependent, {Legacy} legacy",
                noDeps.Count, withDeps.Count, legacyDetectors.Count);

            // Phase 1: Run all independent detectors + legacy detectors in parallel
            var phase1Tasks = new List<Task>();

            foreach (var detector in noDeps)
                phase1Tasks.Add(RunReactiveDetectorAsync(
                    detector, context, signalBus, results, timeoutCts.Token));

            foreach (var detector in legacyDetectors)
                phase1Tasks.Add(RunLegacyDetectorAsync(
                    detector, context, signalBus, results, timeoutCts.Token));

            await Task.WhenAll(phase1Tasks);

            _logger.LogDebug("Phase 1 complete: {SignalCount} signals published",
                signalBus.GetAllSignals().Count);

            // Phase 2: Run dependent detectors as their signals become available
            var phase2Tasks = withDeps.Select(detector =>
                RunDependentDetectorAsync(detector, context, signalBus, results, timeoutCts.Token));

            await Task.WhenAll(phase2Tasks);

            signalBus.Complete();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Detection pipeline timed out after {Timeout}", _overallTimeout);
        }

        sw.Stop();

        return new DetectionPipelineResult
        {
            DetectorResults = results,
            Signals = signalBus.GetAllSignals(),
            ProcessingTimeMs = sw.ElapsedMilliseconds
        };
    }

    private async Task RunReactiveDetectorAsync(
        IReactiveDetector detector,
        HttpContext context,
        IDetectionSignalBus signalBus,
        Dictionary<string, DetectorResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await detector.DetectAsync(context, signalBus, cancellationToken);

            lock (results)
            {
                results[detector.Name] = result;
            }

            _logger.LogDebug("{Detector} completed: confidence={Confidence:F2}, signals={Signals}",
                detector.Name, result.Confidence, string.Join(",", detector.ProducesSignals));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reactive detector {Detector} failed", detector.Name);
        }
    }

    private async Task RunLegacyDetectorAsync(
        IDetector detector,
        HttpContext context,
        IDetectionSignalBus signalBus,
        Dictionary<string, DetectorResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await detector.DetectAsync(context, cancellationToken);

            lock (results)
            {
                results[detector.Name] = result;
            }

            // Publish a generic signal for legacy detectors
            signalBus.Publish(new DetectionSignal
            {
                Key = $"legacy.{detector.Name.ToLowerInvariant().Replace(" ", "_")}.confidence",
                Value = result.Confidence,
                SourceDetector = detector.Name,
                Confidence = result.Confidence
            });

            if (result.BotType.HasValue)
                signalBus.Publish(new DetectionSignal
                {
                    Key = $"legacy.{detector.Name.ToLowerInvariant().Replace(" ", "_")}.bot_type",
                    Value = result.BotType.Value,
                    SourceDetector = detector.Name
                });

            _logger.LogDebug("Legacy {Detector} completed: confidence={Confidence:F2}",
                detector.Name, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Legacy detector {Detector} failed", detector.Name);
        }
    }

    private async Task RunDependentDetectorAsync(
        IReactiveDetector detector,
        HttpContext context,
        IDetectionSignalBus signalBus,
        Dictionary<string, DetectorResult> results,
        CancellationToken cancellationToken)
    {
        try
        {
            // Wait for required signals
            var receivedSignals = await signalBus.WaitForSignalsAsync(
                detector.RequiresSignals,
                detector.SignalTimeout,
                cancellationToken);

            var gotAll = receivedSignals.Count == detector.RequiresSignals.Count;

            if (!gotAll && !detector.CanRunWithPartialSignals)
            {
                _logger.LogDebug(
                    "{Detector} skipped: only received {Got}/{Required} required signals",
                    detector.Name, receivedSignals.Count, detector.RequiresSignals.Count);
                return;
            }

            var result = await detector.DetectAsync(context, signalBus, cancellationToken);

            lock (results)
            {
                results[detector.Name] = result;
            }

            _logger.LogDebug(
                "{Detector} completed (partial={Partial}): confidence={Confidence:F2}",
                detector.Name, !gotAll, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dependent detector {Detector} failed", detector.Name);
        }
    }
}

/// <summary>
///     Result from the reactive detection pipeline
/// </summary>
public class DetectionPipelineResult
{
    /// <summary>
    ///     Results from each detector
    /// </summary>
    public required IReadOnlyDictionary<string, DetectorResult> DetectorResults { get; init; }

    /// <summary>
    ///     All signals published during detection
    /// </summary>
    public required IReadOnlyDictionary<string, DetectionSignal> Signals { get; init; }

    /// <summary>
    ///     Total processing time
    /// </summary>
    public long ProcessingTimeMs { get; init; }
}