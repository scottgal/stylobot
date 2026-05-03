using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Operation-level response coordinator (short-lived, per-request).
///     Runs AFTER request coordinator completes (separate lifecycle).
///     Shares OperationSignalSink with request coordinator (same sink instance).
///     Mode (Blocking/Async) is decided VERY EARLY by request coordinator.
/// </summary>
public sealed class ResponseDetectionOrchestrator : IAsyncDisposable
{
    private readonly SignalSink _globalSignalSink; // Global sink for signature-level coordination
    private readonly ILogger<ResponseDetectionOrchestrator> _logger;
    private readonly SignalSink _operationSignalSink; // SHARED with request orchestrator (operation-scoped)
    private readonly ResponseCoordinatorOptions _options;
    private readonly IEnumerable<IResponseDetector> _responseDetectors;

    public ResponseDetectionOrchestrator(
        ILogger<ResponseDetectionOrchestrator> logger,
        IOptions<BotDetectionOptions> options,
        SignalSink operationSignalSink, // Same sink as request orchestrator (per-request)
        SignalSink globalSignalSink, // Global sink for cross-request signals
        IEnumerable<IResponseDetector> responseDetectors)
    {
        _logger = logger;
        _options = options.Value.ResponseCoordinator ?? new ResponseCoordinatorOptions();
        _operationSignalSink = operationSignalSink;
        _globalSignalSink = globalSignalSink;
        _responseDetectors = responseDetectors;

        _logger.LogInformation(
            "ResponseDetectionOrchestrator initialized with {DetectorCount} detectors",
            _responseDetectors.Count());
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("ResponseDetectionOrchestrator disposed");
        await ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Analyze a response signal. Called from middleware AFTER request orchestrator completes.
    ///     Decision: blocking (inline) or non-blocking (async) is made VERY EARLY from ResponseAnalysisContext.
    /// </summary>
    public async Task<ResponseDetectionResult> AnalyzeResponseAsync(
        ResponseSignal signal,
        ResponseAnalysisContext? analysisContext,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestId = signal.RequestId;

        // Determine mode early (blocking vs non-blocking)
        var isBlocking = analysisContext?.Mode == ResponseAnalysisMode.Inline;
        var thoroughness = analysisContext?.Thoroughness ?? ResponseAnalysisThoroughness.Standard;

        _logger.LogDebug(
            "Starting response analysis for {RequestId}: mode={Mode}, thoroughness={Thoroughness}",
            requestId, analysisContext?.Mode ?? ResponseAnalysisMode.Async, thoroughness);

        // Create response-side blackboard (separate from request blackboard)
        var responseBlackboard = new ResponseBlackboardState
        {
            Signal = signal,
            AnalysisContext = analysisContext,
            Thoroughness = thoroughness,
            OperationSignals = _operationSignalSink, // Access to request-side signals (same sink)
            RequestId = requestId,
            ClientId = signal.ClientId,
            Signals = new Dictionary<string, object>(),
            Contributions = new List<ResponseDetectionContribution>(),
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>()
        };

        // Emit start signal to operation sink (notification pattern)
        _operationSignalSink.Raise("response.analysis.start", requestId);

        try
        {
            // Run response detectors in waves (similar to request-side)
            await RunResponseDetectorsAsync(responseBlackboard, cancellationToken);

            // Aggregate contributions into final score
            var result = AggregateContributions(responseBlackboard, stopwatch.Elapsed);

            // Emit completion signal to operation sink (notification pattern)
            _operationSignalSink.Raise("response.analysis.complete", requestId);
            _operationSignalSink.Raise("response.score", result.ResponseScore.ToString("F4"));

            // Create operation summary and send to GLOBAL sink (for signature-level tracking)
            var operationSummary = new OperationSummarySignal
            {
                Signature = signal.ClientId,
                RequestId = requestId,
                Timestamp = signal.Timestamp,
                Path = signal.Path,
                Method = signal.Method,
                StatusCode = signal.StatusCode,
                RequestBotProbability = signal.RequestBotProbability,
                ResponseScore = result.ResponseScore,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                TriggerSignals = analysisContext?.TriggerSignals ?? new Dictionary<string, object>()
            };

            // Send to global sink for signature-level coordinators (notification pattern)
            _globalSignalSink.Raise($"operation.complete.{signal.ClientId}", requestId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Response analysis failed for {RequestId}", requestId);

            _operationSignalSink.Raise("response.analysis.error", requestId);

            throw;
        }
    }

    /// <summary>
    ///     Run response detectors in waves using EPHEMERAL wave coordination.
    ///     Even 'blocking' mode is async - it just runs in parallel with wave atoms.
    /// </summary>
    private async Task RunResponseDetectorsAsync(
        ResponseBlackboardState state,
        CancellationToken cancellationToken)
    {
        // Filter detectors by thoroughness level
        var enabledDetectors = _responseDetectors
            .Where(d => d.IsEnabled && ShouldRunDetector(d, state.Thoroughness))
            .OrderBy(d => d.Priority)
            .ToList();

        _logger.LogDebug(
            "Running {Count} response detectors for {RequestId} (thoroughness={Thoroughness})",
            enabledDetectors.Count, state.RequestId, state.Thoroughness);

        // Group by priority (wave)
        var waves = enabledDetectors
            .GroupBy(d => d.Priority)
            .OrderBy(g => g.Key)
            .ToList();

        // Execute each wave using ephemeral coordinator
        foreach (var wave in waves)
        {
            var wavePriority = wave.Key;
            var waveDetectors = wave.ToList();

            _logger.LogTrace(
                "Running response detector wave {Priority} with {Count} detectors",
                wavePriority, waveDetectors.Count);

            // Use EphemeralWorkCoordinator for parallel execution with signal preservation
            await using var coordinator = new EphemeralWorkCoordinator<IResponseDetector>(
                async (detector, ct) =>
                {
                    try
                    {
                        var contribution = await detector.DetectAsync(state, ct);

                        if (contribution != null)
                        {
                            // Thread-safe add
                            lock (state.Contributions)
                            {
                                state.Contributions.Add(contribution);
                            }

                            // Emit contribution signal to operation sink (notification pattern)
                            _operationSignalSink.Raise($"response.detector.{detector.Name}",
                                contribution.Score.ToString("F4"));
                        }

                        lock (state.CompletedDetectors)
                        {
                            state.CompletedDetectors.Add(detector.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Response detector {Detector} failed for {RequestId}",
                            detector.Name, state.RequestId);

                        lock (state.FailedDetectors)
                        {
                            state.FailedDetectors.Add(detector.Name);
                        }
                    }
                },
                new EphemeralOptions
                {
                    MaxConcurrency = Environment.ProcessorCount,
                    Signals = _operationSignalSink
                });

            // Enqueue all detectors
            foreach (var detector in waveDetectors) await coordinator.EnqueueAsync(detector, cancellationToken);

            // Drain coordinator (wait for all to complete)
            await coordinator.DrainAsync(cancellationToken);

            // Emit wave complete signal (notification pattern)
            _operationSignalSink.Raise($"response.wave.{wavePriority}.complete", state.RequestId);

            // Update signals for next wave
            state.Signals["_system.completed_detectors"] = state.CompletedDetectors.Count;
            state.Signals[$"_system.wave.{wavePriority}.complete"] = true;
        }
    }

    /// <summary>
    ///     Determine if detector should run based on thoroughness level.
    /// </summary>
    private bool ShouldRunDetector(IResponseDetector detector, ResponseAnalysisThoroughness thoroughness)
    {
        return detector.MinThoroughness <= thoroughness;
    }

    /// <summary>
    ///     Aggregate contributions into final response detection result.
    /// </summary>
    private ResponseDetectionResult AggregateContributions(
        ResponseBlackboardState state,
        TimeSpan elapsed)
    {
        if (state.Contributions.Count == 0)
            return new ResponseDetectionResult
            {
                RequestId = state.RequestId,
                ClientId = state.ClientId,
                ResponseScore = 0.0,
                Confidence = 0.0,
                Contributions = Array.Empty<ResponseDetectionContribution>(),
                TopReasons = Array.Empty<string>(),
                ProcessingTimeMs = elapsed.TotalMilliseconds
            };

        // Weighted average of contributions
        var totalWeight = state.Contributions.Sum(c => c.Weight);
        var weightedScore = state.Contributions.Sum(c => c.Score * c.Weight) / Math.Max(totalWeight, 1.0);

        // Confidence based on agreement between detectors
        var scores = state.Contributions.Select(c => c.Score).ToList();
        var avgScore = scores.Average();
        var variance = scores.Average(s => Math.Pow(s - avgScore, 2));
        var confidence = Math.Max(0.0, 1.0 - Math.Sqrt(variance));

        // Top reasons (ordered by contribution strength)
        var topReasons = state.Contributions
            .Where(c => !string.IsNullOrEmpty(c.Reason))
            .OrderByDescending(c => Math.Abs(c.Score * c.Weight))
            .Take(5)
            .Select(c => c.Reason!)
            .ToArray();

        return new ResponseDetectionResult
        {
            RequestId = state.RequestId,
            ClientId = state.ClientId,
            ResponseScore = Math.Clamp(weightedScore, 0.0, 1.0),
            Confidence = confidence,
            Contributions = state.Contributions,
            TopReasons = topReasons,
            ProcessingTimeMs = elapsed.TotalMilliseconds,
            DetectorCount = state.CompletedDetectors.Count,
            FailedDetectorCount = state.FailedDetectors.Count
        };
    }
}

/// <summary>
///     Blackboard state for response-side detection (separate from request blackboard).
///     Contains response signal + access to operation signal sink (shared with request coordinator).
/// </summary>
public sealed class ResponseBlackboardState
{
    /// <summary>
    ///     The response signal being analyzed
    /// </summary>
    public required ResponseSignal Signal { get; init; }

    /// <summary>
    ///     Analysis context from request-side (if triggered early)
    /// </summary>
    public ResponseAnalysisContext? AnalysisContext { get; init; }

    /// <summary>
    ///     Thoroughness level for this analysis
    /// </summary>
    public required ResponseAnalysisThoroughness Thoroughness { get; init; }

    /// <summary>
    ///     Operation signal sink (shared with request orchestrator, per-request scope).
    ///     Response detectors can read request-side signals from this SAME sink.
    /// </summary>
    public required SignalSink OperationSignals { get; init; }

    /// <summary>
    ///     Request ID (correlates with request-side detection)
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     Client ID (signature hash)
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    ///     Response-side signals (populated by response detectors)
    /// </summary>
    public required Dictionary<string, object> Signals { get; init; }

    /// <summary>
    ///     Contributions from response detectors
    /// </summary>
    public required List<ResponseDetectionContribution> Contributions { get; init; }

    /// <summary>
    ///     Completed detectors
    /// </summary>
    public required HashSet<string> CompletedDetectors { get; init; }

    /// <summary>
    ///     Failed detectors
    /// </summary>
    public required HashSet<string> FailedDetectors { get; init; }

    /// <summary>
    ///     Get a signal from response-side blackboard
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        return Signals.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Get a signal from REQUEST-side via operation sink (same sink, different lifecycle).
    ///     This enables cross-correlation between request and response detection.
    /// </summary>
    public T? GetRequestSignal<T>(string signalName)
    {
        var events = OperationSignals.Sense(evt => evt.Signal == signalName);
        var latest = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();

        if (latest == default || latest.Key == null)
            return default;

        // Parse from Key property
        try
        {
            if (typeof(T) == typeof(string))
                return (T)(object)latest.Key;
            if (typeof(T) == typeof(double) && double.TryParse(latest.Key, out var d))
                return (T)(object)d;
            if (typeof(T) == typeof(int) && int.TryParse(latest.Key, out var i))
                return (T)(object)i;
            if (typeof(T) == typeof(bool) && bool.TryParse(latest.Key, out var b))
                return (T)(object)b;
        }
        catch
        {
        }

        return default;
    }

    /// <summary>
    ///     Check if request-side had a specific signal (cross-correlation).
    ///     Example: if (state.HasRequestSignal("request.honeypot.hit")) { ... }
    /// </summary>
    public bool HasRequestSignal(string signalName)
    {
        return OperationSignals.Sense(evt => evt.Signal == signalName).Any();
    }
}

/// <summary>
///     Operation summary signal sent to global sink for signature-level tracking.
///     Aggregates request + response results for cross-request analysis.
/// </summary>
public sealed record OperationSummarySignal
{
    /// <summary>Signature (client ID hash)</summary>
    public required string Signature { get; init; }

    /// <summary>Request ID (for correlation)</summary>
    public required string RequestId { get; init; }

    /// <summary>When this operation occurred</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Request path</summary>
    public required string Path { get; init; }

    /// <summary>HTTP method</summary>
    public required string Method { get; init; }

    /// <summary>HTTP status code</summary>
    public required int StatusCode { get; init; }

    /// <summary>Request-side bot probability (0.0-1.0)</summary>
    public required double RequestBotProbability { get; init; }

    /// <summary>Response-side bot probability (0.0-1.0)</summary>
    public required double ResponseScore { get; init; }

    /// <summary>Total processing time (ms)</summary>
    public required double ProcessingTimeMs { get; init; }

    /// <summary>Trigger signals from request-side (for context)</summary>
    public required IReadOnlyDictionary<string, object> TriggerSignals { get; init; }
}

/// <summary>
///     Response detector interface (similar to IContributingDetector but for responses).
/// </summary>
public interface IResponseDetector
{
    /// <summary>
    ///     Detector name
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Priority (wave number). Lower = runs first.
    /// </summary>
    int Priority { get; }

    /// <summary>
    ///     Minimum thoroughness level required to run this detector.
    ///     Minimal detectors run always, Deep detectors only for high-risk requests.
    /// </summary>
    ResponseAnalysisThoroughness MinThoroughness => ResponseAnalysisThoroughness.Standard;

    /// <summary>
    ///     Is detector enabled?
    /// </summary>
    bool IsEnabled => true;

    /// <summary>
    ///     Analyze response and return contribution (or null if no contribution).
    /// </summary>
    Task<ResponseDetectionContribution?> DetectAsync(
        ResponseBlackboardState state,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Contribution from a response detector.
/// </summary>
public sealed record ResponseDetectionContribution
{
    /// <summary>
    ///     Detector that generated this contribution
    /// </summary>
    public required string DetectorName { get; init; }

    /// <summary>
    ///     Category of evidence
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    ///     Bot probability score (0.0 = human-like, 1.0 = bot-like)
    /// </summary>
    public required double Score { get; init; }

    /// <summary>
    ///     Weight of this contribution
    /// </summary>
    public double Weight { get; init; } = 1.0;

    /// <summary>
    ///     Reason for this score
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    ///     Processing time for this detector
    /// </summary>
    public double ProcessingTimeMs { get; init; }

    /// <summary>
    ///     Priority/wave when detector ran
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
///     Result of response detection analysis.
/// </summary>
public sealed record ResponseDetectionResult
{
    /// <summary>
    ///     Request ID (correlates with request detection)
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     Client ID (signature hash)
    /// </summary>
    public required string ClientId { get; init; }

    /// <summary>
    ///     Aggregated response score (0.0-1.0)
    /// </summary>
    public required double ResponseScore { get; init; }

    /// <summary>
    ///     Confidence in the score
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    ///     All contributions from detectors
    /// </summary>
    public required IReadOnlyList<ResponseDetectionContribution> Contributions { get; init; }

    /// <summary>
    ///     Top reasons (up to 5)
    /// </summary>
    public required IReadOnlyList<string> TopReasons { get; init; }

    /// <summary>
    ///     Total processing time
    /// </summary>
    public required double ProcessingTimeMs { get; init; }

    /// <summary>
    ///     Number of detectors that ran
    /// </summary>
    public int DetectorCount { get; init; }

    /// <summary>
    ///     Number of detectors that failed
    /// </summary>
    public int FailedDetectorCount { get; init; }
}

/// <summary>
///     Base class for response detectors.
/// </summary>
public abstract class ResponseDetectorBase : IResponseDetector
{
    public abstract string Name { get; }
    public virtual int Priority => 100;
    public virtual ResponseAnalysisThoroughness MinThoroughness => ResponseAnalysisThoroughness.Standard;
    public virtual bool IsEnabled => true;

    public abstract Task<ResponseDetectionContribution?> DetectAsync(
        ResponseBlackboardState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Helper to create a contribution
    /// </summary>
    protected ResponseDetectionContribution CreateContribution(
        string category,
        double score,
        string? reason = null,
        double weight = 1.0,
        double processingTimeMs = 0,
        int priority = 0)
    {
        return new ResponseDetectionContribution
        {
            DetectorName = Name,
            Category = category,
            Score = Math.Clamp(score, 0.0, 1.0),
            Weight = weight,
            Reason = reason,
            ProcessingTimeMs = processingTimeMs,
            Priority = priority
        };
    }
}