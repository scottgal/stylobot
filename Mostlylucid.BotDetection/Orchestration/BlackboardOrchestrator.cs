using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Configuration for the blackboard orchestrator
/// </summary>
public class OrchestratorOptions
{
    /// <summary>
    ///     Maximum time for the entire detection pipeline.
    ///     Default: 5 seconds
    /// </summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Maximum number of waves before stopping.
    ///     Prevents infinite loops from circular trigger dependencies.
    ///     Default: 10
    /// </summary>
    public int MaxWaves { get; set; } = 10;

    /// <summary>
    ///     Time to wait between waves for new triggers to become satisfied.
    ///     Default: 50ms
    /// </summary>
    public TimeSpan WaveInterval { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    ///     Minimum bot probability to trigger expensive detectors.
    ///     Saves resources on obvious humans.
    ///     Default: 0.3
    /// </summary>
    public double ExpensiveDetectorThreshold { get; set; } = 0.3;

    /// <summary>
    ///     Circuit breaker: number of failures before disabling a detector.
    ///     Default: 5
    /// </summary>
    public int CircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    ///     Circuit breaker: time to wait before retrying a disabled detector.
    ///     Default: 60 seconds
    /// </summary>
    public TimeSpan CircuitBreakerResetTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Whether to enable parallel execution of detectors.
    ///     Default: true
    /// </summary>
    public bool EnableParallelExecution { get; set; } = true;

    /// <summary>
    ///     Maximum parallel detectors per wave (global limit across all waves).
    ///     This acts as a global ceiling - individual waves can't exceed this.
    ///     Set to 1 for fully sequential execution.
    ///     Default: 10
    /// </summary>
    public int MaxParallelDetectors { get; set; } = 10;

    /// <summary>
    ///     Per-wave parallelism overrides. Allows fine-tuning parallelism by wave number.
    ///     Key: Wave number (0-based), Value: Max parallelism for that wave.
    ///     Example: { [0] = 8, [1] = 4, [2] = 2 } means Wave 0 uses 8 parallel, Wave 1 uses 4, Wave 2 uses 2.
    ///     If not specified for a wave, uses MaxParallelDetectors as default.
    ///     Useful for: High parallelism for fast detectors (Wave 0), low parallelism for AI/LLM (Wave 2+).
    ///     Default: empty (all waves use MaxParallelDetectors)
    /// </summary>
    public Dictionary<int, int> ParallelismPerWave { get; set; } = new();

    // ==========================================
    // Quorum Settings (for early exit optimization)
    // ==========================================

    /// <summary>
    ///     Enable quorum-based early exit.
    ///     When enabled, detection can exit early if enough detectors agree on a verdict.
    ///     Default: true
    /// </summary>
    public bool EnableQuorumExit { get; set; } = true;

    /// <summary>
    ///     Minimum number of detectors that must complete before making a verdict.
    ///     Prevents false positives from a single detector.
    ///     Default: 3
    /// </summary>
    public int MinDetectorsForVerdict { get; set; } = 3;

    /// <summary>
    ///     Quorum timeout - maximum time to wait for MinDetectorsForVerdict.
    ///     After this, use whatever evidence is available.
    ///     Default: 100ms
    /// </summary>
    public TimeSpan QuorumTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    ///     Bot probability threshold for quorum "definitely bot" consensus.
    ///     If average score across quorum exceeds this, exit early as bot.
    ///     Default: 0.75
    /// </summary>
    public double QuorumBotThreshold { get; set; } = 0.75;

    /// <summary>
    ///     Bot probability threshold for quorum "definitely human" consensus.
    ///     If average score across quorum is below this, exit early as human.
    ///     Default: 0.25
    /// </summary>
    public double QuorumHumanThreshold { get; set; } = 0.25;

    /// <summary>
    ///     Number of detectors that must agree on "uncertain" (0.4-0.6) to escalate to AI.
    ///     Default: 3
    /// </summary>
    public int UncertainQuorumForAiEscalation { get; set; } = 3;

    // ==========================================
    // Ephemeral Signal Sink Settings
    // ==========================================

    /// <summary>
    ///     Maximum signals to retain in the global signal sink.
    ///     Higher values = more observability, more memory.
    ///     Default: 5000
    /// </summary>
    public int SignalSinkMaxCapacity { get; set; } = 5000;

    /// <summary>
    ///     Maximum age for signals in the global sink before eviction.
    ///     Default: 5 minutes
    /// </summary>
    public TimeSpan SignalSinkMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Maximum capacity for contribution (typed payload) signal sink.
    ///     Default: 1000
    /// </summary>
    public int ContributionSignalSinkMaxCapacity { get; set; } = 1000;

    /// <summary>
    ///     Maximum age for contribution signals.
    ///     Default: 5 minutes
    /// </summary>
    public TimeSpan ContributionSignalSinkMaxAge { get; set; } = TimeSpan.FromMinutes(5);

    // ==========================================
    // Ephemeral Circuit Breaker Settings (Decaying)
    // ==========================================

    /// <summary>
    ///     Maximum entries in the circuit breaker decay window.
    ///     Default: 100 (one per detector)
    /// </summary>
    public int CircuitBreakerMaxEntries { get; set; } = 100;

    /// <summary>
    ///     Score reduction on success (negative = heals faster).
    ///     Default: -2.0
    /// </summary>
    public double CircuitBreakerSuccessHealAmount { get; set; } = -2.0;

    /// <summary>
    ///     Score increase on failure.
    ///     Default: 1.0
    /// </summary>
    public double CircuitBreakerFailurePenalty { get; set; } = 1.0;

    /// <summary>
    ///     Half-open threshold multiplier. Circuit transitions to half-open
    ///     when score is between Threshold and Threshold * this value.
    ///     Default: 1.5
    /// </summary>
    public double CircuitBreakerHalfOpenMultiplier { get; set; } = 1.5;

    // ==========================================
    // Ephemeral Work Coordinator Settings
    // ==========================================

    /// <summary>
    ///     Maximum tracked operations per wave (operations * this multiplier).
    ///     Default: 2
    /// </summary>
    public int EphemeralTrackedOperationsMultiplier { get; set; } = 2;

    /// <summary>
    ///     Maximum operation lifetime in ephemeral coordinator.
    ///     Default: 30 seconds
    /// </summary>
    public TimeSpan EphemeralMaxOperationLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Per-request signal sink capacity.
    ///     Default: 200
    /// </summary>
    public int RequestSignalSinkCapacity { get; set; } = 200;

    /// <summary>
    ///     Per-request signal sink max age.
    ///     Default: 30 seconds
    /// </summary>
    public TimeSpan RequestSignalSinkMaxAge { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
///     Wave-based parallel orchestrator using blackboard architecture.
///     Execution model:
///     1. Wave 0: Run all detectors with no trigger conditions (in parallel)
///     2. Wave N: Run all detectors whose triggers are now satisfied (in parallel)
///     3. Repeat until no more detectors can run, early exit, or timeout
///     Key features:
///     - Parallel execution within waves
///     - Circuit breaker per detector
///     - Timeout handling at detector and pipeline level
///     - Early exit on verified bots
///     - Real-time signal aggregation
/// </summary>
public class BlackboardOrchestrator
{
    // Object pool for reusing state collections
    private static readonly ObjectPool<PooledDetectionState> StatePool = DetectionStatePoolFactory.Create();

    // Circuit breaker state per detector
    private readonly ConcurrentDictionary<string, CircuitState> _circuitStates = new();
    private readonly BotClusterService? _clusterService;
    private readonly CountryReputationTracker? _countryTracker;
    private readonly IContributingDetector[] _sortedDetectors;
    private readonly ILearningEventBus? _learningBus;
    private readonly LlmClassificationCoordinator? _llmCoordinator;
    private readonly ILogger<BlackboardOrchestrator> _logger;
    private readonly BotDetectionOptions _fullOptions;
    private readonly OrchestratorOptions _options;
    private readonly PiiHasher _piiHasher;
    private readonly IPolicyEvaluator? _policyEvaluator;
    private readonly IPolicyRegistry? _policyRegistry;
    private readonly SignatureCoordinator? _signatureCoordinator;

    [ThreadStatic] private static Random? t_random;

    public BlackboardOrchestrator(
        ILogger<BlackboardOrchestrator> logger,
        IOptions<BotDetectionOptions> options,
        IEnumerable<IContributingDetector> detectors,
        PiiHasher piiHasher,
        ILearningEventBus? learningBus = null,
        IPolicyRegistry? policyRegistry = null,
        IPolicyEvaluator? policyEvaluator = null,
        SignatureCoordinator? signatureCoordinator = null,
        LlmClassificationCoordinator? llmCoordinator = null,
        CountryReputationTracker? countryTracker = null,
        BotClusterService? clusterService = null)
    {
        _logger = logger;
        _fullOptions = options.Value;
        _options = options.Value.Orchestrator;
        _sortedDetectors = detectors.OrderBy(d => d.Priority).ToArray();
        _piiHasher = piiHasher;
        _learningBus = learningBus;
        _policyRegistry = policyRegistry;
        _policyEvaluator = policyEvaluator;
        _signatureCoordinator = signatureCoordinator;
        _llmCoordinator = llmCoordinator;
        _countryTracker = countryTracker;
        _clusterService = clusterService;
    }

    /// <summary>
    ///     Run the full detection pipeline and aggregate results.
    ///     Uses the default policy or path-based policy resolution.
    /// </summary>
    public Task<AggregatedEvidence> DetectAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default)
    {
        // Resolve policy based on request path
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
    ///     Run the full detection pipeline with a specific policy.
    /// </summary>
    public virtual async Task<AggregatedEvidence> DetectWithPolicyAsync(
        HttpContext httpContext,
        DetectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var requestId = httpContext.TraceIdentifier;

        // Use policy timeout if shorter than orchestrator timeout
        var timeout = policy.Timeout < _options.TotalTimeout ? policy.Timeout : _options.TotalTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        // Get pooled state to reuse collections
        var pooledState = StatePool.Get();
        try
        {
            var aggregator = new DetectionLedger(requestId);
            var signals = pooledState.Signals;
            var completedDetectors = pooledState.CompletedDetectors;
            var failedDetectors = pooledState.FailedDetectors;
            var ranDetectors = pooledState.RanDetectors;
            PolicyAction? finalAction = null;
            string? triggeredActionPolicyName = null;

            // Wire up zero-allocation key set wrappers for BuildState
            var completedKeys = pooledState.CompletedDetectorKeys;
            completedKeys.SetSource(completedDetectors);
            var failedKeys = pooledState.FailedDetectorKeys;
            failedKeys.SetSource(failedDetectors);

            // Build detector lists based on policy
            var allPolicyDetectors = pooledState.AllPolicyDetectors;
            allPolicyDetectors.UnionWith(policy.FastPathDetectors);
            allPolicyDetectors.UnionWith(policy.SlowPathDetectors);
            allPolicyDetectors.UnionWith(policy.AiPathDetectors);

            // Get enabled detectors (respecting circuit breakers and policy)
            // _sortedDetectors is pre-sorted at construction time, so no per-request sort.
            var availableDetectors = new List<IContributingDetector>(_sortedDetectors.Length);
            foreach (var d in _sortedDetectors)
            {
                if (d.IsEnabled && IsCircuitClosed(d.Name) &&
                    (allPolicyDetectors.Count == 0 || allPolicyDetectors.Contains(d.Name)))
                    availableDetectors.Add(d);
            }

            _logger.LogDebug(
                "Starting detection for {RequestId} with policy {Policy}, {DetectorCount} available detectors",
                requestId, policy.Name, availableDetectors.Count);

            var waveNumber = 0;
            var aiRan = false; // Track whether AI detectors executed (affects probability clamping)

            try
            {
                while (waveNumber < _options.MaxWaves && !cts.Token.IsCancellationRequested)
                {
                    // Build current blackboard state
                    var state = BuildState(
                        httpContext,
                        signals,
                        completedKeys,
                        failedKeys,
                        aggregator,
                        requestId,
                        stopwatch.Elapsed,
                        aiRan);

                    // Find detectors that can run in this wave — no LINQ allocation
                    var readyDetectorsList = new List<IContributingDetector>();
                    foreach (var d in availableDetectors)
                    {
                        if (!ranDetectors.Contains(d.Name) &&
                            (policy.BypassTriggerConditions || CanRun(d, state.Signals)))
                            readyDetectorsList.Add(d);
                    }

                    if (readyDetectorsList.Count == 0)
                    {
                        _logger.LogDebug(
                            "Wave {Wave}: No more detectors ready, finishing",
                            waveNumber);
                        break;
                    }

                    _logger.LogDebug(
                        "Wave {Wave}: Running {Count} detectors: {Names}",
                        waveNumber,
                        readyDetectorsList.Count,
                        string.Join(", ", readyDetectorsList.Select(d => d.Name)));

                    // Mark as ran (before execution to prevent re-triggering)
                    foreach (var detector in readyDetectorsList) ranDetectors.Add(detector.Name);

                    // Execute wave
                    await ExecuteWaveAsync(
                        readyDetectorsList,
                        state,
                        aggregator,
                        signals,
                        completedDetectors,
                        failedDetectors,
                        cts.Token);

                    // Check for early exit - but still run policy evaluation for transitions
                    var earlyExitTriggered = false;
                    if (aggregator.EarlyExit && aggregator.EarlyExitContribution is { } exitContrib)
                    {
                        _logger.LogInformation(
                            "Early exit triggered by {Detector}: {Verdict} - {Reason}",
                            exitContrib.DetectorName,
                            exitContrib.EarlyExitVerdict,
                            exitContrib.Reason);
                        earlyExitTriggered = true;
                        // Don't break yet - fall through to policy evaluation for transitions
                    }

                    // Update system signals for next wave (or for policy evaluation on early exit)
                    signals[DetectorCountTrigger.CompletedDetectorsSignal] = completedDetectors.Count;
                    signals[RiskThresholdTrigger.CurrentRiskSignal] = aggregator.BotProbability;

                    // Evaluate policy transitions — skip on Wave 0 so that Wave 1
                    // triggered detectors (VersionAge, Inconsistency, Heuristic, etc.)
                    // get a chance to contribute before the early-exit threshold fires.
                    // Detector-driven early exits (EarlyExit flag above) still apply on all waves.
                    if (_policyEvaluator != null && waveNumber > 0)
                    {
                        var evalState = BuildState(
                            httpContext,
                            signals,
                            completedKeys,
                            failedKeys,
                            aggregator,
                            requestId,
                            stopwatch.Elapsed,
                            aiRan);

                        var evalResult = _policyEvaluator.Evaluate(policy, evalState);

                        if (!evalResult.ShouldContinue)
                        {
                            // Check for action policy name first (takes precedence)
                            if (!string.IsNullOrEmpty(evalResult.ActionPolicyName))
                            {
                                triggeredActionPolicyName = evalResult.ActionPolicyName;
                                _logger.LogDebug(
                                    "Policy {Policy} triggered action policy {ActionPolicy}: {Reason}",
                                    policy.Name, evalResult.ActionPolicyName, evalResult.Reason);
                                break;
                            }

                            if (evalResult.Action.HasValue)
                            {
                                // Handle EscalateToAi specially - run AI detectors then continue
                                if (evalResult.Action.Value == PolicyAction.EscalateToAi)
                                {
                                    // Default AI detectors if none specified in policy
                                    var knownAiDetectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                        { "Onnx", "Llm" };
                                    var aiDetectorNames = policy.AiPathDetectors.Count > 0
                                        ? policy.AiPathDetectors.ToHashSet(StringComparer.OrdinalIgnoreCase)
                                        : knownAiDetectors; // Empty = run ALL known AI detectors

                                    // Get AI detectors that haven't run yet
                                    var aiDetectors = new List<IContributingDetector>();
                                    foreach (var d in _sortedDetectors)
                                    {
                                        if (d.IsEnabled && IsCircuitClosed(d.Name) &&
                                            aiDetectorNames.Contains(d.Name) &&
                                            !ranDetectors.Contains(d.Name))
                                            aiDetectors.Add(d);
                                    }

                                    if (aiDetectors.Count > 0)
                                    {
                                        _logger.LogDebug(
                                            "Policy {Policy} escalating to AI, running {Count} AI detectors: {Names}",
                                            policy.Name,
                                            aiDetectors.Count,
                                            string.Join(", ", aiDetectors.Select(d => d.Name)));

                                        // Mark as ran
                                        foreach (var detector in aiDetectors) ranDetectors.Add(detector.Name);

                                        // Build fresh state so AI detectors see current risk/contributions
                                        var aiState = BuildState(
                                            httpContext, signals, completedKeys,
                                            failedKeys, aggregator, requestId,
                                            stopwatch.Elapsed);

                                        // Execute AI detectors
                                        await ExecuteWaveAsync(
                                            aiDetectors,
                                            aiState,
                                            aggregator,
                                            signals,
                                            completedDetectors,
                                            failedDetectors,
                                            cts.Token);

                                        // Mark that AI detectors have run (removes probability clamping)
                                        aiRan = true;

                                        // Update signals after AI ran
                                        signals[DetectorCountTrigger.CompletedDetectorsSignal] =
                                            completedDetectors.Count;
                                        signals[RiskThresholdTrigger.CurrentRiskSignal] =
                                            aggregator.BotProbability;

                                        // Continue to allow early exit check after AI
                                        waveNumber++;
                                        continue;
                                    }
                                }

                                // For other actions (Block, Allow, Challenge), set final action and exit
                                finalAction = evalResult.Action.Value;
                                _logger.LogDebug(
                                    "Policy {Policy} triggered action {Action}: {Reason}",
                                    policy.Name, evalResult.Action, evalResult.Reason);
                                break;
                            }

                            if (!string.IsNullOrEmpty(evalResult.NextPolicy) && _policyRegistry != null)
                            {
                                var nextPolicy = _policyRegistry.GetPolicy(evalResult.NextPolicy);
                                if (nextPolicy != null)
                                {
                                    _logger.LogDebug(
                                        "Policy transition: {From} -> {To}",
                                        policy.Name, nextPolicy.Name);
                                    policy = nextPolicy;
                                    // Continue with new policy's detectors
                                }
                            }
                        }
                    }

                    // If early exit was triggered, stop the wave loop (but after policy evaluation)
                    if (earlyExitTriggered) break;

                    waveNumber++;

                    // Signals propagate immediately via ConcurrentDictionary (no delay needed).
                    // Yield to avoid starving other tasks, but don't burn 50ms per wave.
                    if (waveNumber < _options.MaxWaves) await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                     !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Detection timed out after {Elapsed}ms for {RequestId}",
                    stopwatch.ElapsedMilliseconds, requestId);
            }

            var result = aggregator.ToAggregatedEvidence(policy.Name, aiRan: aiRan, premergedSignals: signals);

            // Always use stopwatch for actual wall-clock time (more accurate than sum of contributions)
            var actualProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            // Apply policy action and timing to result
            // Mark EarlyExit=true when policy triggered an early action (Allow/Block before full pipeline)
            var wasEarlyExit = finalAction.HasValue &&
                               (finalAction.Value == PolicyAction.Allow || finalAction.Value == PolicyAction.Block) &&
                               result.ContributingDetectors.Count < availableDetectors.Count;

            if (finalAction.HasValue || !string.IsNullOrEmpty(triggeredActionPolicyName))
                result = result with
                {
                    PolicyAction = finalAction,
                    TriggeredActionPolicyName = triggeredActionPolicyName,
                    PolicyName = policy.Name,
                    TotalProcessingTimeMs = actualProcessingTimeMs,
                    EarlyExit = wasEarlyExit || result.EarlyExit,
                    EarlyExitVerdict = wasEarlyExit
                        ? (finalAction!.Value == PolicyAction.Allow ? EarlyExitVerdict.PolicyAllowed : EarlyExitVerdict.PolicyBlocked)
                        : result.EarlyExitVerdict
                };
            else
                result = result with
                {
                    PolicyName = policy.Name,
                    TotalProcessingTimeMs = actualProcessingTimeMs
                };

            // Skip heavy bookkeeping for verified good bot early exits (e.g. Googlebot).
            // These don't need learning events, signature coordination, or LLM classification.
            var isVerifiedEarlyExit = result.EarlyExit &&
                result.EarlyExitVerdict is EarlyExitVerdict.VerifiedGoodBot or EarlyExitVerdict.Whitelisted;

            // Also skip learning for reputation-driven early exits to break the
            // positive feedback loop: Reputation → Detection → Learning → Reputation.
            // Reputation detectors echoing their own prior decisions is circular reasoning.
            var isReputationDriven = result.EarlyExit &&
                result.Ledger?.EarlyExitContribution?.DetectorName is "FastPathReputation" or "ReputationBias";

            if (!isVerifiedEarlyExit)
            {
                // Only publish learning events for non-reputation-driven detections.
                // Reputation-driven results are just echoes of prior beliefs — not new evidence.
                if (!isReputationDriven)
                    PublishLearningEvent(result, httpContext, requestId, stopwatch.Elapsed);

                // Extract geo data from signals for country tracking and cluster analysis
                var geoCountryCode = signals.TryGetValue("geo.country_code", out var ccVal) ? ccVal as string : null;
                var geoCountryName = signals.TryGetValue("geo.country_name", out var cnVal) ? cnVal as string : null;
                var geoAsn = signals.TryGetValue("request.ip.asn", out var asnVal) ? asnVal as string : null;
                var geoIsDatacenter = signals.TryGetValue("request.ip.is_datacenter", out var dcVal) && dcVal is true;

                // Feed country reputation tracker
                if (_countryTracker != null && !string.IsNullOrEmpty(geoCountryCode))
                {
                    _countryTracker.RecordDetection(
                        geoCountryCode,
                        geoCountryName ?? geoCountryCode,
                        result.BotProbability > 0.5,
                        result.BotProbability);
                }

                // Notify cluster service of bot detections to trigger early clustering
                if (_clusterService != null && result.BotProbability > 0.5)
                    _clusterService.NotifyBotDetected();

                // Record request in cross-request signature coordinator
                if (_signatureCoordinator != null)
                    try
                    {
                        var signature = ComputeSignatureHash(httpContext);
                        var path = httpContext.Request.Path.ToString();

                        // Compute IP hash for convergence analysis (detects UA rotation from same IP)
                        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
                        var ipHash = !string.IsNullOrEmpty(clientIp) ? _piiHasher.HashIp(clientIp) : null;

                        // Fire-and-forget (don't await to avoid blocking request)
                        // Pass geo data for cluster analysis
                        _ = _signatureCoordinator.RecordRequestAsync(
                            signature,
                            requestId,
                            path,
                            result.BotProbability,
                            new Dictionary<string, object>(signals),
                            new HashSet<string>(result.ContributingDetectors),
                            cancellationToken,
                            countryCode: geoCountryCode,
                            asn: geoAsn,
                            isDatacenter: geoIsDatacenter,
                            ipHash: ipHash);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail request if signature recording fails
                        _logger.LogWarning(ex, "Failed to record request in SignatureCoordinator for {RequestId}",
                            requestId);
                    }

                // Enqueue for background LLM classification if appropriate
                if (_llmCoordinator != null && _fullOptions.EnableLlmDetection)
                    TryEnqueueLlmClassification(httpContext, result, signals);
            }

            _logger.LogDebug(
                "Detection complete for {RequestId}: {RiskBand} (prob={Probability:F2}, conf={Confidence:F2}) in {Elapsed}ms, {Waves} waves, {Detectors} detectors",
                requestId,
                result.RiskBand,
                result.BotProbability,
                result.Confidence,
                stopwatch.ElapsedMilliseconds,
                waveNumber,
                result.ContributingDetectors.Count);

            return result;
        }
        finally
        {
            // Return pooled state for reuse
            StatePool.Return(pooledState);
        }
    }

    private async Task ExecuteWaveAsync(
        IReadOnlyList<IContributingDetector> detectors,
        BlackboardState state,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ConcurrentDictionary<string, bool> completedDetectors,
        ConcurrentDictionary<string, bool> failedDetectors,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableParallelExecution || detectors.Count == 1)
        {
            // Sequential execution
            foreach (var detector in detectors)
            {
                await ExecuteDetectorAsync(
                    detector, state, aggregator, signals,
                    completedDetectors, failedDetectors, cancellationToken);

                if (aggregator.EarlyExit)
                    break;
            }
        }
        else
        {
            // Parallel execution with semaphore — allocated per-wave intentionally.
            // A class-level semaphore would throttle across concurrent requests, not per-request.
            // Use explicit array instead of LINQ .Select() to avoid closure allocations.
            using var semaphore = new SemaphoreSlim(_options.MaxParallelDetectors);
            var tasks = new Task[detectors.Count];
            for (var i = 0; i < detectors.Count; i++)
            {
                var detector = detectors[i];
                tasks[i] = RunWithSemaphoreAsync(
                    semaphore, detector, state, aggregator, signals,
                    completedDetectors, failedDetectors, cancellationToken);
            }

            await Task.WhenAll(tasks);
        }
    }

    private async Task RunWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        IContributingDetector detector,
        BlackboardState state,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ConcurrentDictionary<string, bool> completedDetectors,
        ConcurrentDictionary<string, bool> failedDetectors,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            await ExecuteDetectorAsync(
                detector, state, aggregator, signals,
                completedDetectors, failedDetectors, cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task ExecuteDetectorAsync(
        IContributingDetector detector,
        BlackboardState state,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, object> signals,
        ConcurrentDictionary<string, bool> completedDetectors,
        ConcurrentDictionary<string, bool> failedDetectors,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(detector.ExecutionTimeout);

            var contributions = await detector.ContributeAsync(state, cts.Token);

            stopwatch.Stop();

            foreach (var contribution in contributions)
            {
                // Set processing time and priority for pipeline ordering
                var withMetadata = contribution with
                {
                    ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                    Priority = detector.Priority
                };
                aggregator.AddContribution(withMetadata);

                // Merge signals
                foreach (var signal in contribution.Signals) signals[signal.Key] = signal.Value;
            }

            completedDetectors[detector.Name] = true;
            RecordSuccess(detector.Name);

            _logger.LogDebug(
                "Detector {Name} completed in {Elapsed}ms with {ContributionCount} contributions",
                detector.Name, stopwatch.ElapsedMilliseconds, contributions.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Detector timeout
            stopwatch.Stop();
            HandleDetectorFailure(detector, aggregator, failedDetectors, "Timeout", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            HandleDetectorFailure(detector, aggregator, failedDetectors, ex.Message, stopwatch.ElapsedMilliseconds);

            _logger.LogWarning(ex,
                "Detector {Name} failed after {Elapsed}ms: {Message}",
                detector.Name, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }

    private void HandleDetectorFailure(
        IContributingDetector detector,
        DetectionLedger aggregator,
        ConcurrentDictionary<string, bool> failedDetectors,
        string reason,
        double elapsedMs)
    {
        failedDetectors[detector.Name] = true;
        aggregator.RecordFailure(detector.Name);
        RecordFailure(detector.Name);

        if (!detector.IsOptional)
            _logger.LogError(
                "Required detector {Name} failed: {Reason}",
                detector.Name, reason);
    }

    private static bool CanRun(IContributingDetector detector, IReadOnlyDictionary<string, object> signals)
    {
        // No conditions = can always run
        if (detector.TriggerConditions.Count == 0)
            return true;

        // All conditions must be satisfied
        return detector.TriggerConditions.All(c => c.IsSatisfied(signals));
    }

    private static BlackboardState BuildState(
        HttpContext httpContext,
        IReadOnlyDictionary<string, object> signals,
        IReadOnlySet<string> completedDetectors,
        IReadOnlySet<string> failedDetectors,
        DetectionLedger aggregator,
        string requestId,
        TimeSpan elapsed,
        bool aiRan = false)
    {
        // Read BotProbability and Confidence directly from the ledger
        // instead of calling ToAggregatedEvidence() which allocates heavily.
        // Apply the same clamping logic as DetectionLedgerExtensions.
        var botProbability = aggregator.BotProbability;
        if (!aiRan)
            botProbability = Math.Clamp(botProbability, 0.05, 0.80);

        // SignalWriter: give detectors direct write access to the shared signal dict,
        // so they can call state.WriteSignal() instead of allocating ImmutableDictionary per contribution.
        var signalWriter = signals as ConcurrentDictionary<string, object>;

        return new BlackboardState
        {
            HttpContext = httpContext,
            Signals = signals, // Pass by reference - no copy
            SignalWriter = signalWriter,
            CurrentRiskScore = botProbability,
            DetectionConfidence = aggregator.Confidence,
            CompletedDetectors = completedDetectors, // Zero-copy: ConcurrentDictionaryKeySet wrapper
            FailedDetectors = failedDetectors,
            Contributions = aggregator.Contributions,
            RequestId = requestId,
            Elapsed = elapsed
        };
    }

    #region Circuit Breaker

    private bool IsCircuitClosed(string detectorName)
    {
        if (!_circuitStates.TryGetValue(detectorName, out var state))
            return true;

        return state.AllowRequest(_options.CircuitBreakerResetTime);
    }

    private void RecordSuccess(string detectorName)
    {
        if (_circuitStates.TryGetValue(detectorName, out var state))
            state.RecordSuccess();
    }

    private void RecordFailure(string detectorName)
    {
        var state = _circuitStates.GetOrAdd(detectorName, _ => new CircuitState());

        if (state.RecordFailure(_options.CircuitBreakerThreshold))
            _logger.LogWarning(
                "Circuit breaker opened for detector {Name} after {Count} failures",
                detectorName, state.FailureCount);
    }

    #endregion

    #region Learning Events

    private void PublishLearningEvent(
        AggregatedEvidence result,
        HttpContext httpContext,
        string requestId,
        TimeSpan elapsed)
    {
        if (_learningBus == null)
            return;

        // Determine learning event type based on BOTH probability and confidence:
        // 1. High confidence + extreme probability → HighConfidenceDetection (ground truth for training)
        // 2. Low confidence + significant probability → FullDetection (uncertain → trigger full learning)
        // The learning pipeline runs ALL detectors for uncertain cases, boosting confidence.
        var isHighConfidenceBot = result.BotProbability >= 0.8 && result.Confidence >= 0.7;
        var isHighConfidenceHuman = result.BotProbability <= 0.2 && result.Confidence >= 0.7;
        var isUncertain = result.Confidence < 0.6 && result.BotProbability is >= 0.3 and <= 0.8;

        var eventType = isHighConfidenceBot || isHighConfidenceHuman
            ? LearningEventType.HighConfidenceDetection
            : LearningEventType.FullDetection;

        // Include UA, IP, path for signature extraction
        var metadata = new Dictionary<string, object>
        {
            ["botProbability"] = result.BotProbability,
            ["detectionConfidence"] = result.Confidence,
            ["uncertain"] = isUncertain,
            ["riskBand"] = result.RiskBand.ToString(),
            ["contributingDetectors"] = result.ContributingDetectors.ToList(),
            ["failedDetectors"] = result.FailedDetectors.ToList(),
            ["processingTimeMs"] = elapsed.TotalMilliseconds,
            ["categoryBreakdown"] = result.CategoryBreakdown.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.Score)
        };

        // Add signature data from signals (required for weight learning)
        if (result.Signals.TryGetValue(SignalKeys.UserAgent, out var ua))
            metadata["userAgent"] = ua;
        if (result.Signals.TryGetValue(SignalKeys.ClientIp, out var ip))
            metadata["ip"] = ip;
        if (result.Signals.TryGetValue("path", out var path))
            metadata["path"] = path;

        // Extract features for similarity learning vector storage.
        // Without this, SimilarityLearningHandler gets null features and skips AddAsync.
        // Also extract for uncertain detections — the learning pipeline needs features to improve.
        Dictionary<string, double>? features = null;
        if (isHighConfidenceBot || isHighConfidenceHuman || isUncertain)
        {
            try
            {
                var floatFeatures = HeuristicFeatureExtractor.ExtractFeatures(httpContext, result);
                features = new Dictionary<string, double>(floatFeatures.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var (key, value) in floatFeatures)
                    features[key] = value;
            }
            catch
            {
                // Feature extraction is non-critical; don't fail the learning event
            }
        }

        _learningBus.TryPublish(new LearningEvent
        {
            Type = eventType,
            Source = nameof(BlackboardOrchestrator),
            Confidence = result.Confidence,
            Label = result.BotProbability >= 0.5,
            RequestId = requestId,
            Features = features,
            Metadata = metadata
        });
    }

    /// <summary>
    ///     Compute privacy-preserving signature hash from client IP + UA using HMAC-SHA256.
    ///     CRITICAL: This must use HMAC-SHA256 (cryptographic), NOT XxHash64!
    ///     Returns the PRIMARY signature (IP + UA composite).
    /// </summary>
    private string ComputeSignatureHash(HttpContext httpContext)
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        // Use injected PiiHasher for HMAC-SHA256 (cryptographic, non-reversible)
        // Key is managed via DI configuration (from config/vault)
        return _piiHasher.ComputeSignature(clientIp, userAgent);
    }

    #endregion

    #region Background LLM Enqueue

    /// <summary>
    ///     Decide whether to enqueue this detection for background LLM classification.
    ///     Runs after detection completes so we have full AggregatedEvidence + signals.
    ///     Fire-and-forget: never blocks the request pipeline.
    /// </summary>
    private void TryEnqueueLlmClassification(
        HttpContext httpContext,
        AggregatedEvidence result,
        ConcurrentDictionary<string, object> signals)
    {
        try
        {
            var coordOptions = _fullOptions.LlmCoordinator;
            var prob = result.BotProbability;

            // Read TimescaleDB signals
            var isNew = signals.TryGetValue("ts.is_new", out var newVal) && newVal is true;
            var isConclusive = signals.TryGetValue("ts.is_conclusive", out var concVal) && concVal is true;

            // Determine enqueue reason and sampling decision
            string? enqueueReason = null;
            var isDriftSample = false;
            var isConfirmationSample = false;
            var confidence = result.Confidence;

            if (isNew)
            {
                // New unknown signature — always enqueue for learning
                enqueueReason = "new_signature";
            }
            else if (isConclusive && confidence >= 0.7)
            {
                // Already well-classified by TimescaleDB AND confident — skip
                _logger.LogDebug("Skipping LLM enqueue: TimescaleDB reputation is conclusive and confidence is high ({Confidence:F2})", confidence);
                return;
            }
            else if (confidence < 0.6 && prob >= 0.3)
            {
                // Low confidence with meaningful probability — always enqueue for deeper analysis.
                // This is the key trigger: "we're not sure" → get LLM opinion to boost confidence.
                enqueueReason = "low_confidence";
            }
            else if (prob >= coordOptions.MinProbabilityToEnqueue && prob <= coordOptions.MaxProbabilityToEnqueue)
            {
                // Ambiguous range — always enqueue
                enqueueReason = "ambiguous";
            }
            else if (prob > coordOptions.MaxProbabilityToEnqueue)
            {
                // High-risk — sample at reduced rate for confirmation
                var random = t_random ??= new Random();
                if (random.NextDouble() < coordOptions.HighRiskConfirmationRate)
                {
                    enqueueReason = "confirmation";
                    isConfirmationSample = true;
                }
                else
                {
                    return;
                }
            }
            else if (prob < coordOptions.MinProbabilityToEnqueue)
            {
                // Low-risk — adaptive sampling for drift detection
                var random = t_random ??= new Random();
                var sampleRate = _llmCoordinator!.GetAdaptiveSampleRate();
                if (random.NextDouble() < sampleRate)
                {
                    enqueueReason = "drift_sample";
                    isDriftSample = true;
                }
                else
                {
                    return;
                }
            }

            if (enqueueReason == null)
                return;

            // Compute multi-vector signatures for churn-resistant identity
            var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext.Request.Headers.UserAgent.ToString();
            var signature = _piiHasher.ComputeSignature(clientIp, userAgent);
            if (string.IsNullOrEmpty(signature))
                return;

            var signatureVectors = new Dictionary<string, string> { ["primary"] = signature };
            if (!string.IsNullOrEmpty(clientIp))
            {
                signatureVectors["ip"] = _piiHasher.HashIp(clientIp);
                signatureVectors["subnet"] = _piiHasher.HashIpSubnet(clientIp);
            }
            if (!string.IsNullOrEmpty(userAgent))
                signatureVectors["ua"] = _piiHasher.HashUserAgent(userAgent);

            // Build compact request info snapshot
            var requestInfo = BuildSnapshotRequestInfo(httpContext, result);

            var topReasons = result.Contributions
                .Where(c => !string.IsNullOrEmpty(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason!)
                .ToList();

            var request = new LlmClassificationRequest
            {
                RequestId = httpContext.TraceIdentifier,
                PrimarySignature = signature,
                UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
                PreBuiltRequestInfo = requestInfo,
                HeuristicProbability = prob,
                TopReasons = topReasons,
                Signals = new Dictionary<string, object>(result.Signals),
                BotType = result.PrimaryBotType?.ToString(),
                BotName = result.PrimaryBotName,
                Path = httpContext.Request.Path.Value,
                Method = httpContext.Request.Method,
                Confidence = result.Confidence,
                RiskBand = result.RiskBand.ToString(),
                Action = result.PolicyAction?.ToString() ?? result.TriggeredActionPolicyName ?? "Allow",
                IsNewSignature = isNew,
                SignatureVectors = signatureVectors,
                IsDriftSample = isDriftSample,
                IsConfirmationSample = isConfirmationSample,
                EnqueueReason = enqueueReason
            };

            _llmCoordinator!.TryEnqueue(request);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue LLM classification for {RequestId}",
                httpContext.TraceIdentifier);
        }
    }

    /// <summary>
    ///     Builds ultra-compact request info for background LLM analysis.
    ///     Same format as LlmDetector.BuildRequestInfo but uses AggregatedEvidence
    ///     instead of HttpContext.Items (which may not be populated yet).
    /// </summary>
    private static string BuildSnapshotRequestInfo(HttpContext httpContext, AggregatedEvidence evidence)
    {
        var sb = new StringBuilder();
        sb.Append($"prob={evidence.BotProbability:F2}\n");

        var ua = httpContext.Request.Headers.UserAgent.ToString();
        sb.Append($"ua=\"{(ua.Length > 100 ? ua[..97] + "..." : ua)}\"\n");

        var lang = httpContext.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(lang))
            sb.Append($"lang=\"{(lang.Length > 30 ? lang[..27] + "..." : lang)}\"\n");

        var hasCookies = httpContext.Request.Cookies.Any();
        if (hasCookies) sb.Append("cookies=1\n");
        sb.Append($"hdrs={httpContext.Request.Headers.Count}\n");

        // Top detector signals
        var topHits = evidence.Contributions
            .Where(c => Math.Abs(c.ConfidenceDelta) >= 0.1)
            .OrderByDescending(c => Math.Abs(c.ConfidenceDelta) * c.Weight)
            .Take(3)
            .ToList();

        if (topHits.Count != 0)
        {
            sb.Append("[top]\n");
            foreach (var c in topHits)
            {
                var name = c.DetectorName switch
                {
                    "Heuristic" => "H", "UserAgent" => "UA", "Header" => "Hdr",
                    "Ip" => "IP", "Behavioral" => "Beh", "SecurityTool" => "Sec",
                    _ => c.DetectorName.Length > 3 ? c.DetectorName[..3] : c.DetectorName
                };
                var reason = c.Reason.Length > 20 ? c.Reason[..17] + "..." : c.Reason;
                sb.Append($"{name}={c.ConfidenceDelta:+0.0;-0.0}|\"{reason}\"\n");
            }
        }

        if (evidence.PrimaryBotType.HasValue && evidence.PrimaryBotType != BotType.Unknown)
            sb.Append($"type={evidence.PrimaryBotType}\n");

        return sb.ToString();
    }

    #endregion
}

/// <summary>
///     Thread-safe circuit breaker state for a single detector.
///     All mutations are protected by a lock to prevent race conditions.
/// </summary>
internal class CircuitState
{
    private readonly object _lock = new();
    private CircuitBreakerState _state = CircuitBreakerState.Closed;
    private int _failureCount;
    private DateTimeOffset _lastFailure;

    public CircuitBreakerState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>
    ///     Checks if the circuit allows a request through.
    ///     Atomically transitions Open → HalfOpen when reset time has elapsed.
    /// </summary>
    public bool AllowRequest(TimeSpan resetTime)
    {
        lock (_lock)
        {
            if (_state == CircuitBreakerState.Closed)
                return true;

            if (_state == CircuitBreakerState.Open)
            {
                if (DateTimeOffset.UtcNow - _lastFailure > resetTime)
                {
                    _state = CircuitBreakerState.HalfOpen;
                    return true;
                }
                return false;
            }

            // HalfOpen: allow one attempt
            return true;
        }
    }

    /// <summary>
    ///     Records a successful execution. Resets the circuit to Closed.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            _failureCount = 0;
            _state = CircuitBreakerState.Closed;
        }
    }

    /// <summary>
    ///     Records a failed execution. Opens the circuit if threshold is reached.
    ///     Returns true if the circuit just opened.
    /// </summary>
    public bool RecordFailure(int threshold)
    {
        lock (_lock)
        {
            _failureCount++;
            _lastFailure = DateTimeOffset.UtcNow;

            if (_failureCount >= threshold)
            {
                _state = CircuitBreakerState.Open;
                return true;
            }
            return false;
        }
    }

    public int FailureCount
    {
        get { lock (_lock) return _failureCount; }
    }
}

internal enum CircuitBreakerState
{
    Closed, // Normal operation
    Open, // Failing, reject requests
    HalfOpen // Trying one request to see if recovered
}