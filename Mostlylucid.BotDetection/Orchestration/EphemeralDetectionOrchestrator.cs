using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Typed payload for contributor signals - carries evidence through the ephemeral system.
/// </summary>
public readonly record struct ContributionPayload(
    string DetectorName,
    string Category,
    double ConfidenceDelta,
    double Weight,
    long ProcessingTimeMs);

/// <summary>
///     Result of quorum-based early exit evaluation.
/// </summary>
public readonly record struct QuorumVerdict(
    string Verdict,
    double AverageScore,
    int CompletedCount,
    int ExpectedCount,
    bool ShouldExit,
    bool ShouldEscalateToAi);

/// <summary>
///     Wave-based parallel orchestrator using ephemeral 1.1 features:
///     - ContributorTracker for consensus/quorum tracking
///     - StagedPipelineBuilder for wave-based execution
///     - DecayingReputationWindow for circuit breaker with decay
///     - EarlyExitResultCoordinator for short-circuit on verdict
///     - DetectionLedger for contribution accumulation
/// </summary>
public class EphemeralDetectionOrchestrator : IAsyncDisposable
{
    // Decaying reputation window for circuit breaker (failures decay over time)
    private readonly DecayingReputationWindow<string> _circuitBreakerScores;
    private readonly IEnumerable<IContributingDetector> _detectors;

    // Global signal sink for observability across requests
    private readonly SignalSink _globalSignals;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<EphemeralDetectionOrchestrator> _logger;
    private readonly OrchestratorOptions _options;
    private readonly IPolicyEvaluator? _policyEvaluator;
    private readonly IPolicyRegistry? _policyRegistry;

    public EphemeralDetectionOrchestrator(
        ILogger<EphemeralDetectionOrchestrator> logger,
        IOptions<BotDetectionOptions> options,
        IEnumerable<IContributingDetector> detectors,
        ILearningEventBus? learningBus = null,
        IPolicyRegistry? policyRegistry = null,
        IPolicyEvaluator? policyEvaluator = null)
    {
        _logger = logger;
        _options = options.Value.Orchestrator;
        _detectors = detectors;
        _learningBus = learningBus;
        _policyRegistry = policyRegistry;
        _policyEvaluator = policyEvaluator;

        // Global signal sink for observability (configurable)
        _globalSignals = new SignalSink(
            _options.SignalSinkMaxCapacity,
            _options.SignalSinkMaxAge);

        // Circuit breaker with decay - failures decay over time (configurable)
        _circuitBreakerScores = new DecayingReputationWindow<string>(
            _options.CircuitBreakerResetTime,
            _options.CircuitBreakerMaxEntries);
    }

    /// <summary>
    ///     Check if any detector circuit is currently open.
    /// </summary>
    public bool HasOpenCircuits => _globalSignals.Detect(s => s.StartsWith("circuit.open"));

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Get recent signals from the global signal sink for observability.
    /// </summary>
    public IReadOnlyList<SignalEvent> GetRecentSignals()
    {
        return _globalSignals.Sense();
    }


    /// <summary>
    ///     Run the full detection pipeline and aggregate results.
    /// </summary>
    public Task<AggregatedEvidence> DetectAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        var policy = _policyRegistry?.GetPolicyForPath(httpContext.Request.Path)
                     ?? DetectionPolicy.Default;
        return DetectWithPolicyAsync(httpContext, policy, cancellationToken);
    }

    /// <summary>
    ///     Run the detection pipeline with a specific named policy.
    /// </summary>
    public Task<AggregatedEvidence> DetectAsync(
        HttpContext httpContext,
        string policyName,
        CancellationToken cancellationToken = default)
    {
        var policy = _policyRegistry?.GetPolicy(policyName)
                     ?? DetectionPolicy.Default;
        return DetectWithPolicyAsync(httpContext, policy, cancellationToken);
    }

    /// <summary>
    ///     Run the full detection pipeline using ephemeral 1.1 patterns.
    /// </summary>
    public virtual async Task<AggregatedEvidence> DetectWithPolicyAsync(
        HttpContext httpContext,
        DetectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestId = httpContext.TraceIdentifier;

        // Per-request signal sink (configurable)
        var requestSignals = new SignalSink(
            _options.RequestSignalSinkCapacity,
            _options.RequestSignalSinkMaxAge);

        // Use policy timeout if shorter than orchestrator timeout
        var timeout = policy.Timeout < _options.TotalTimeout ? policy.Timeout : _options.TotalTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var aggregator = new DetectionLedger(requestId);
        var signals = new ConcurrentDictionary<string, object>();
        PolicyAction? finalAction = null;
        string? triggeredActionPolicyName = null;

        // Build detector lists based on policy
        var allPolicyDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        allPolicyDetectors.UnionWith(policy.FastPathDetectors);
        allPolicyDetectors.UnionWith(policy.SlowPathDetectors);
        allPolicyDetectors.UnionWith(policy.AiPathDetectors);

        // Get enabled detectors (respecting circuit breakers, policy, and exclusions)
        var availableDetectors = _detectors
            .Where(d => d.IsEnabled && IsCircuitClosed(d.Name))
            .Where(d => allPolicyDetectors.Count == 0 || allPolicyDetectors.Contains(d.Name))
            .Where(d => !policy.ExcludedDetectors.Contains(d.Name))
            .OrderBy(d => d.Priority)
            .ToList();

        _logger.LogDebug(
            "Starting ephemeral detection for {RequestId} with policy {Policy}, {DetectorCount} available detectors",
            requestId, policy.Name, availableDetectors.Count);

        // Create contributor tracker for consensus
        var contributorTracker = new ContributorTracker<IReadOnlyList<DetectionContribution>>(
            availableDetectors.Select(d => d.Name));

        // Group detectors by wave (priority-based)
        var wave0Detectors = availableDetectors.Where(d => d.TriggerConditions.Count == 0).ToList();
        var triggeredDetectors = availableDetectors.Where(d => d.TriggerConditions.Count > 0).ToList();

        var waveNumber = 0;
        var ranDetectors = new HashSet<string>();
        QuorumVerdict? quorumVerdict = null;

        try
        {
            // Wave 0: Run all detectors with no trigger conditions
            // Use quorum-based early exit if enabled
            if (wave0Detectors.Count > 0)
            {
                requestSignals.Raise($"wave.started:{waveNumber}", requestId);
                _globalSignals.Raise($"wave.started:{waveNumber}", requestId);

                _logger.LogDebug(
                    "Wave {Wave}: Running {Count} detectors: {Names}",
                    waveNumber,
                    wave0Detectors.Count,
                    string.Join(", ", wave0Detectors.Select(d => d.Name)));

                // Execute with quorum waiting
                quorumVerdict = await ExecuteWaveWithQuorumAsync(
                    wave0Detectors,
                    httpContext,
                    aggregator,
                    signals,
                    contributorTracker,
                    requestSignals,
                    requestId,
                    stopwatch,
                    cts.Token);

                foreach (var d in wave0Detectors)
                    ranDetectors.Add(d.Name);

                requestSignals.Raise($"wave.completed:{waveNumber}", requestId);
                waveNumber++;

                // Check for quorum-based early exit
                if (quorumVerdict != null)
                {
                    _logger.LogInformation(
                        "Quorum early exit: {Verdict} (score={Score:F2}, {Completed}/{Expected} detectors)",
                        quorumVerdict.Value.Verdict,
                        quorumVerdict.Value.AverageScore,
                        quorumVerdict.Value.CompletedCount,
                        quorumVerdict.Value.ExpectedCount);

                    requestSignals.Raise($"detection.quorum_exit:{quorumVerdict.Value.Verdict}", requestId);
                    _globalSignals.Raise($"detection.quorum_exit:{quorumVerdict.Value.Verdict}", requestId);
                }

                // Check for detector-level early exit (verified bot, etc.)
                if (aggregator.EarlyExit)
                {
                    var exitContrib = aggregator.EarlyExitContribution!;
                    _logger.LogInformation(
                        "Early exit triggered by {Detector}: {Verdict} - {Reason}",
                        exitContrib.DetectorName,
                        exitContrib.EarlyExitVerdict,
                        exitContrib.Reason);

                    requestSignals.Raise("detection.early_exit", requestId);
                    _globalSignals.Raise("detection.early_exit", requestId);
                }
            }

            // Subsequent waves: Run triggered detectors
            // Skip if quorum already reached a definitive verdict
            while (!aggregator.EarlyExit &&
                   quorumVerdict?.ShouldExit != true &&
                   waveNumber < _options.MaxWaves &&
                   !cts.Token.IsCancellationRequested)
            {
                // Update system signals
                signals[DetectorCountTrigger.CompletedDetectorsSignal] = contributorTracker.CompletedCount;
                signals[RiskThresholdTrigger.CurrentRiskSignal] = aggregator.BotProbability;

                // Find detectors ready to run this wave
                var signalSnapshot = new Dictionary<string, object>(signals);
                var readyDetectors = triggeredDetectors
                    .Where(d => !ranDetectors.Contains(d.Name))
                    .Where(d => policy.BypassTriggerConditions || CanRun(d, signalSnapshot))
                    .ToList();

                if (readyDetectors.Count == 0)
                {
                    _logger.LogDebug("Wave {Wave}: No more detectors ready, finishing", waveNumber);
                    break;
                }

                requestSignals.Raise($"wave.started:{waveNumber}", requestId);

                _logger.LogDebug(
                    "Wave {Wave}: Running {Count} detectors: {Names}",
                    waveNumber,
                    readyDetectors.Count,
                    string.Join(", ", readyDetectors.Select(d => d.Name)));

                await ExecuteWaveWithTrackerAsync(
                    readyDetectors,
                    httpContext,
                    aggregator,
                    signals,
                    contributorTracker,
                    requestSignals,
                    requestId,
                    stopwatch,
                    waveNumber,
                    cts.Token);

                foreach (var d in readyDetectors)
                    ranDetectors.Add(d.Name);

                requestSignals.Raise($"wave.completed:{waveNumber}", requestId);

                // Check for early exit
                if (aggregator.EarlyExit)
                {
                    var exitContrib = aggregator.EarlyExitContribution!;
                    _logger.LogInformation(
                        "Early exit triggered by {Detector}: {Verdict} - {Reason}",
                        exitContrib.DetectorName,
                        exitContrib.EarlyExitVerdict,
                        exitContrib.Reason);

                    requestSignals.Raise("detection.early_exit", requestId);
                    _globalSignals.Raise("detection.early_exit", requestId);
                    break;
                }

                // Evaluate policy transitions
                if (_policyEvaluator != null)
                {
                    var evalState = BuildState(httpContext, signals, contributorTracker, aggregator, requestId,
                        stopwatch.Elapsed);
                    var evalResult = _policyEvaluator.Evaluate(policy, evalState);

                    if (!evalResult.ShouldContinue)
                    {
                        if (!string.IsNullOrEmpty(evalResult.ActionPolicyName))
                        {
                            triggeredActionPolicyName = evalResult.ActionPolicyName;
                            break;
                        }

                        if (evalResult.Action.HasValue)
                        {
                            if (evalResult.Action.Value == PolicyAction.EscalateToAi)
                            {
                                // Run AI detectors
                                var aiDetectors = GetAiDetectors(policy, ranDetectors);
                                if (aiDetectors.Count > 0)
                                {
                                    await ExecuteWaveWithTrackerAsync(
                                        aiDetectors,
                                        httpContext,
                                        aggregator,
                                        signals,
                                        contributorTracker,
                                        requestSignals,
                                        requestId,
                                        stopwatch,
                                        waveNumber,
                                        cts.Token);

                                    foreach (var d in aiDetectors)
                                        ranDetectors.Add(d.Name);

                                    waveNumber++;
                                    continue;
                                }
                            }

                            finalAction = evalResult.Action.Value;
                            break;
                        }

                        if (!string.IsNullOrEmpty(evalResult.NextPolicy) && _policyRegistry != null)
                        {
                            var nextPolicy = _policyRegistry.GetPolicy(evalResult.NextPolicy);
                            if (nextPolicy != null) policy = nextPolicy;
                        }
                    }
                }

                waveNumber++;

                if (waveNumber < _options.MaxWaves) await Task.Delay(_options.WaveInterval, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Detection timed out after {Elapsed}ms for {RequestId}",
                stopwatch.ElapsedMilliseconds, requestId);
        }

        var result = aggregator.ToAggregatedEvidence(policy.Name);
        var actualProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;

        var wasEarlyExit = finalAction.HasValue &&
                           (finalAction.Value == PolicyAction.Allow || finalAction.Value == PolicyAction.Block) &&
                           result.ContributingDetectors.Count < 9;

        if (finalAction.HasValue || !string.IsNullOrEmpty(triggeredActionPolicyName))
            result = result with
            {
                PolicyAction = finalAction,
                TriggeredActionPolicyName = triggeredActionPolicyName,
                PolicyName = policy.Name,
                TotalProcessingTimeMs = actualProcessingTimeMs,
                EarlyExit = wasEarlyExit || result.EarlyExit,
                EarlyExitVerdict = wasEarlyExit ? EarlyExitVerdict.PolicyAllowed : result.EarlyExitVerdict
            };
        else
            result = result with
            {
                PolicyName = policy.Name,
                TotalProcessingTimeMs = actualProcessingTimeMs
            };

        // Emit pipeline complete signal
        _globalSignals.Raise(
            $"detection.completed:{result.RiskBand}:{result.BotProbability:F2}",
            requestId);

        PublishLearningEvent(result, requestId, stopwatch.Elapsed);

        _logger.LogDebug(
            "Ephemeral detection complete for {RequestId}: {RiskBand} (prob={Probability:F2}) in {Elapsed}ms, {Waves} waves, quorum: {Completed}/{Total}",
            requestId,
            result.RiskBand,
            result.BotProbability,
            stopwatch.ElapsedMilliseconds,
            waveNumber,
            contributorTracker.CompletedCount,
            contributorTracker.ExpectedCount);

        return result;
    }

    private List<IContributingDetector> GetAiDetectors(DetectionPolicy policy, HashSet<string> ranDetectors)
    {
        var knownAiDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Onnx", "Llm" };
        var aiDetectorNames = policy.AiPathDetectors.Count > 0
            ? policy.AiPathDetectors.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : knownAiDetectors;

        return _detectors
            .Where(d => d.IsEnabled && IsCircuitClosed(d.Name))
            .Where(d => aiDetectorNames.Contains(d.Name))
            .Where(d => !ranDetectors.Contains(d.Name))
            .OrderBy(d => d.Priority)
            .ToList();
    }

    /// <summary>
    ///     Execute a wave with quorum-based early exit.
    ///     Waits for MinDetectorsForVerdict or QuorumTimeout, then evaluates consensus.
    /// </summary>
    private async Task<QuorumVerdict?> ExecuteWaveWithQuorumAsync(
        IReadOnlyList<IContributingDetector> detectors,
        HttpContext httpContext,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ContributorTracker<IReadOnlyList<DetectionContribution>> tracker,
        SignalSink requestSignals,
        string requestId,
        Stopwatch pipelineStopwatch,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableQuorumExit || detectors.Count < _options.MinDetectorsForVerdict)
        {
            // Not enough detectors for quorum - run all and wait
            await ExecuteWaveWithTrackerAsync(
                detectors, httpContext, aggregator, signals, tracker,
                requestSignals, requestId, pipelineStopwatch, 0, cancellationToken);
            return null;
        }

        // Start all detectors in parallel
        var detectorTasks = new List<Task>();

        var ephemeralOptions = new EphemeralOptions
        {
            MaxConcurrency = _options.MaxParallelDetectors,
            MaxTrackedOperations = detectors.Count * _options.EphemeralTrackedOperationsMultiplier,
            MaxOperationLifetime = _options.EphemeralMaxOperationLifetime,
            Signals = requestSignals,
            OnSignal = evt => _globalSignals.Raise(evt),
            CancelOnSignals = new HashSet<string> { "detection.early_exit", "detection.quorum_exit:*" }
        };

        // Fire off all detectors
        var executionTask = detectors.EphemeralForEachAsync(
            async (detector, ct) =>
            {
                await ExecuteDetectorWithTrackerAsync(
                    detector, httpContext, aggregator, signals, tracker,
                    requestSignals, requestId, pipelineStopwatch, ct);
            },
            ephemeralOptions,
            cancellationToken);

        // Wait for quorum OR timeout
        var quorumResult = await tracker.WaitForQuorumAsync(
            Math.Min(_options.MinDetectorsForVerdict, detectors.Count),
            _options.QuorumTimeout,
            cancellationToken);

        // Evaluate quorum verdict
        QuorumVerdict? verdict = null;

        if (quorumResult.Reached || quorumResult.CompletedCount > 0)
        {
            var currentEvidence = aggregator.ToAggregatedEvidence();
            var avgScore = currentEvidence.BotProbability;

            // Check for definitive verdict
            if (avgScore >= _options.QuorumBotThreshold)
            {
                verdict = new QuorumVerdict(
                    "DefinitelyBot",
                    avgScore,
                    quorumResult.CompletedCount,
                    detectors.Count,
                    true,
                    false);
            }
            else if (avgScore <= _options.QuorumHumanThreshold)
            {
                verdict = new QuorumVerdict(
                    "DefinitelyHuman",
                    avgScore,
                    quorumResult.CompletedCount,
                    detectors.Count,
                    true,
                    false);
            }
            else
            {
                // Check for "uncertain" quorum that should escalate to AI
                var uncertainCount = currentEvidence.Contributions
                    .Count(c => c.ConfidenceDelta > -0.1 && c.ConfidenceDelta < 0.1);

                if (uncertainCount >= _options.UncertainQuorumForAiEscalation)
                    verdict = new QuorumVerdict(
                        "Uncertain",
                        avgScore,
                        quorumResult.CompletedCount,
                        detectors.Count,
                        false,
                        true);
            }
        }

        // If we got a definitive verdict, we can skip waiting for remaining detectors
        if (verdict?.ShouldExit != true)
            // Wait for remaining detectors to complete
            try
            {
                await executionTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected if we're cancelling
            }

        return verdict;
    }

    /// <summary>
    ///     Execute a wave using ContributorTracker for consensus.
    /// </summary>
    private async Task ExecuteWaveWithTrackerAsync(
        IReadOnlyList<IContributingDetector> detectors,
        HttpContext httpContext,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ContributorTracker<IReadOnlyList<DetectionContribution>> tracker,
        SignalSink requestSignals,
        string requestId,
        Stopwatch pipelineStopwatch,
        int waveNumber,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableParallelExecution || detectors.Count == 1)
        {
            // Sequential execution
            foreach (var detector in detectors)
            {
                await ExecuteDetectorWithTrackerAsync(
                    detector, httpContext, aggregator, signals, tracker,
                    requestSignals, requestId, pipelineStopwatch, cancellationToken);

                if (aggregator.EarlyExit)
                    break;
            }

            return;
        }

        // Parallel execution with ephemeral (configurable per-wave)
        // Get parallelism for this wave - use wave-specific if configured, otherwise global max
        var waveParallelism = _options.ParallelismPerWave.TryGetValue(waveNumber, out var waveMax)
            ? Math.Min(waveMax, _options.MaxParallelDetectors) // Respect global ceiling
            : _options.MaxParallelDetectors;

        var ephemeralOptions = new EphemeralOptions
        {
            MaxConcurrency = waveParallelism,
            MaxTrackedOperations = detectors.Count * _options.EphemeralTrackedOperationsMultiplier,
            MaxOperationLifetime = _options.EphemeralMaxOperationLifetime,
            Signals = requestSignals,
            OnSignal = evt => _globalSignals.Raise(evt),
            CancelOnSignals = new HashSet<string> { "detection.early_exit" }
        };

        await detectors.EphemeralForEachAsync(
            async (detector, ct) =>
            {
                await ExecuteDetectorWithTrackerAsync(
                    detector, httpContext, aggregator, signals, tracker,
                    requestSignals, requestId, pipelineStopwatch, ct);
            },
            ephemeralOptions,
            cancellationToken);
    }

    private async Task ExecuteDetectorWithTrackerAsync(
        IContributingDetector detector,
        HttpContext httpContext,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ContributorTracker<IReadOnlyList<DetectionContribution>> tracker,
        SignalSink requestSignals,
        string requestId,
        Stopwatch pipelineStopwatch,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        requestSignals.Raise($"detector.started:{detector.Name}", requestId);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(detector.ExecutionTimeout);

            var state = BuildState(
                httpContext,
                signals,
                tracker,
                aggregator,
                requestId,
                pipelineStopwatch.Elapsed);

            var contributions = await detector.ContributeAsync(state, cts.Token);
            stopwatch.Stop();

            foreach (var contribution in contributions)
            {
                var withMetadata = contribution with
                {
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    Priority = detector.Priority
                };
                aggregator.AddContribution(withMetadata);

                // Emit contribution signal to global sink for observability
                _globalSignals.Raise(
                    $"contribution.{detector.Name}.{contribution.Category}",
                    requestId);

                foreach (var signal in contribution.Signals) signals[signal.Key] = signal.Value;
            }

            // Report success to tracker
            tracker.Complete(detector.Name, contributions);
            RecordSuccess(detector.Name);

            requestSignals.Raise(
                $"detector.completed:{detector.Name}:{stopwatch.ElapsedMilliseconds}ms",
                requestId);

            // Emit progress signal
            ProgressSignals.Emit(
                requestSignals,
                "detection",
                tracker.CompletedCount,
                tracker.ExpectedCount,
                1);

            _logger.LogDebug(
                "Detector {Name} completed in {Elapsed}ms with {ContributionCount} contributions",
                detector.Name, stopwatch.ElapsedMilliseconds, contributions.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            tracker.Fail(detector.Name, new TimeoutException("Detector timed out"));
            HandleDetectorFailure(detector, aggregator, requestSignals, "Timeout", stopwatch.ElapsedMilliseconds,
                requestId);
            requestSignals.Raise($"detector.timeout:{detector.Name}", requestId);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            tracker.Fail(detector.Name, ex);
            HandleDetectorFailure(detector, aggregator, requestSignals, ex.Message, stopwatch.ElapsedMilliseconds,
                requestId);

            _logger.LogWarning(ex,
                "Detector {Name} failed after {Elapsed}ms: {Message}",
                detector.Name, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private void HandleDetectorFailure(
        IContributingDetector detector,
        DetectionLedger aggregator,
        SignalSink requestSignals,
        string reason,
        double elapsedMs,
        string requestId)
    {
        aggregator.RecordFailure(detector.Name);
        RecordFailure(detector.Name);

        requestSignals.Raise($"detector.failed:{detector.Name}:{reason}", requestId);
        _globalSignals.Raise($"detector.failed:{detector.Name}", requestId);

        if (!detector.IsOptional)
            _logger.LogError(
                "Required detector {Name} failed: {Reason}",
                detector.Name, reason);
    }

    private static bool CanRun(IContributingDetector detector, IReadOnlyDictionary<string, object> signals)
    {
        if (detector.TriggerConditions.Count == 0)
            return true;

        return detector.TriggerConditions.All(c => c.IsSatisfied(signals));
    }

    private static BlackboardState BuildState(
        HttpContext httpContext,
        ConcurrentDictionary<string, object> signals,
        ContributorTracker<IReadOnlyList<DetectionContribution>> tracker,
        DetectionLedger aggregator,
        string requestId,
        TimeSpan elapsed)
    {
        var aggregated = aggregator.ToAggregatedEvidence();
        var completedResults = tracker.GetCompletedResults();

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = new Dictionary<string, object>(signals),
            CurrentRiskScore = aggregated.BotProbability,
            DetectionConfidence = aggregated.Confidence,
            CompletedDetectors = completedResults.Select(r => r.Contributor).ToHashSet(),
            FailedDetectors = aggregated.FailedDetectors,
            Contributions = aggregated.Contributions,
            RequestId = requestId,
            Elapsed = elapsed
        };
    }

    #region Learning Events

    private void PublishLearningEvent(
        AggregatedEvidence result,
        string requestId,
        TimeSpan elapsed)
    {
        if (_learningBus == null)
            return;

        var isHighConfidenceBot = result.BotProbability >= 0.8;
        var isHighConfidenceHuman = result.BotProbability <= 0.2;
        var eventType = isHighConfidenceBot || isHighConfidenceHuman
            ? LearningEventType.HighConfidenceDetection
            : LearningEventType.FullDetection;

        var metadata = new Dictionary<string, object>
        {
            ["botProbability"] = result.BotProbability,
            ["riskBand"] = result.RiskBand.ToString(),
            ["contributingDetectors"] = result.ContributingDetectors.ToList(),
            ["failedDetectors"] = result.FailedDetectors.ToList(),
            ["processingTimeMs"] = elapsed.TotalMilliseconds,
            ["categoryBreakdown"] = result.CategoryBreakdown.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.Score)
        };

        if (result.Signals.TryGetValue(Models.SignalKeys.UserAgent, out var ua))
            metadata["userAgent"] = ua;
        if (result.Signals.TryGetValue(Models.SignalKeys.ClientIp, out var ip))
            metadata["ip"] = ip;
        if (result.Signals.TryGetValue("path", out var path))
            metadata["path"] = path;

        _learningBus.TryPublish(new LearningEvent
        {
            Type = eventType,
            Source = nameof(EphemeralDetectionOrchestrator),
            Confidence = result.Confidence,
            Label = result.BotProbability >= 0.5,
            RequestId = requestId,
            Metadata = metadata
        });
    }

    #endregion

    // Signal keys for type-safe signal emission
    public static class SignalKeys
    {
        public static readonly SignalKey DetectorStarted = new("detector.started");
        public static readonly SignalKey DetectorCompleted = new("detector.completed");
        public static readonly SignalKey DetectorFailed = new("detector.failed");
        public static readonly SignalKey DetectorTimeout = new("detector.timeout");
        public static readonly SignalKey WaveStarted = new("wave.started");
        public static readonly SignalKey WaveCompleted = new("wave.completed");
        public static readonly SignalKey EarlyExit = new("detection.early_exit");
        public static readonly SignalKey PipelineCompleted = new("detection.completed");
        public static readonly SignalKey CircuitOpen = new("circuit.open");
        public static readonly SignalKey CircuitHalfOpen = new("circuit.half_open");
        public static readonly SignalKey CircuitClosed = new("circuit.closed");

        // Typed key for contributions
        public static readonly SignalKey<ContributionPayload> Contribution = new("contribution");
    }

    #region Circuit Breaker (with decaying reputation window)

    private bool IsCircuitClosed(string detectorName)
    {
        var failureScore = _circuitBreakerScores.GetScore(detectorName);

        // Score decays toward 0 over time
        // Circuit opens when score >= threshold
        if (failureScore >= _options.CircuitBreakerThreshold)
        {
            // Check if we should try half-open (configurable multiplier)
            if (failureScore < _options.CircuitBreakerThreshold * _options.CircuitBreakerHalfOpenMultiplier)
            {
                _globalSignals.Raise($"circuit.half_open:{detectorName}");
                return true; // Allow one attempt
            }

            return false; // Circuit open
        }

        return true; // Circuit closed
    }

    private void RecordSuccess(string detectorName)
    {
        // Reduce failure score on success (configurable heal amount)
        _circuitBreakerScores.Update(detectorName, _options.CircuitBreakerSuccessHealAmount);

        if (_circuitBreakerScores.GetScore(detectorName) < 0) _globalSignals.Raise($"circuit.closed:{detectorName}");
    }

    private void RecordFailure(string detectorName)
    {
        // Increase failure score on failure (configurable penalty)
        var newScore = _circuitBreakerScores.Update(detectorName, _options.CircuitBreakerFailurePenalty);

        if (newScore >= _options.CircuitBreakerThreshold)
        {
            _globalSignals.Raise($"circuit.open:{detectorName}");
            _logger.LogWarning(
                "Circuit breaker opened for detector {Name}, failure score: {Score:F1}",
                detectorName, newScore);
        }
    }

    #endregion
}