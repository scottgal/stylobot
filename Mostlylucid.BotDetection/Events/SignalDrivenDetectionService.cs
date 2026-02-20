using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Signal-driven detection service.
///     Analyzers run in order and emit signals; listeners react to signals they care about.
///     Uses consensus-based finalisation - waits for all detectors to report before finalising.
/// </summary>
public class SignalDrivenDetectionService
{
    // Detector names for consensus tracking
    private static readonly string[] CoreDetectors =
    [
        "User-Agent Detector",
        "Header Detector",
        "IP Detector",
        "Behavioral Detector",
        "Inconsistency Detector"
    ];

    private readonly BehavioralDetector _behavioralAnalyzer;
    private readonly IBotSignalBusFactory _busFactory;
    private readonly ClientSideDetector? _clientSideAnalyzer;
    private readonly HeaderDetector _headerAnalyzer;
    private readonly HeuristicDetector? _heuristicAnalyzer;
    private readonly InconsistencyDetector _inconsistencyAnalyzer;
    private readonly IpDetector _ipAnalyzer;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<SignalDrivenDetectionService> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;

    // Core analyzers (run in order, emit signals)
    private readonly UserAgentDetector _userAgentAnalyzer;

    public SignalDrivenDetectionService(
        ILogger<SignalDrivenDetectionService> logger,
        IOptions<BotDetectionOptions> options,
        IBotSignalBusFactory busFactory,
        UserAgentDetector userAgentAnalyzer,
        HeaderDetector headerAnalyzer,
        IpDetector ipAnalyzer,
        BehavioralDetector behavioralAnalyzer,
        InconsistencyDetector inconsistencyAnalyzer,
        ClientSideDetector? clientSideAnalyzer = null,
        HeuristicDetector? heuristicAnalyzer = null,
        ILearningEventBus? learningBus = null,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _busFactory = busFactory;
        _userAgentAnalyzer = userAgentAnalyzer;
        _headerAnalyzer = headerAnalyzer;
        _ipAnalyzer = ipAnalyzer;
        _behavioralAnalyzer = behavioralAnalyzer;
        _clientSideAnalyzer = clientSideAnalyzer;
        _inconsistencyAnalyzer = inconsistencyAnalyzer;
        _heuristicAnalyzer = heuristicAnalyzer;
        _learningBus = learningBus;
        _metrics = metrics;
    }

    public async Task<BotDetectionResult> DetectAsync(
        HttpContext httpContext,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Create detection context (shared state)
        var context = new DetectionContext
        {
            HttpContext = httpContext,
            CancellationToken = ct
        };

        // Create signal bus with all listeners wired up
        await using var bus = _busFactory.Create();

        // Register expected detectors for consensus
        var expectedDetectors = GetExpectedDetectors();
        bus.ExpectDetectors(expectedDetectors);

        try
        {
            // === Stage 0: Raw signal extraction (parallel-safe, no deps) ===

            // User-Agent analysis
            var uaResult = await RunDetectorAsync(
                _userAgentAnalyzer, httpContext, context, bus, ct);

            // Check for early exit (e.g., verified bot found)
            if (bus.ShouldEarlyExit)
            {
                _logger.LogDebug("Early exit triggered after User-Agent detection");
                return await FinaliseAsync(context, bus, sw, ct);
            }

            // Header analysis
            await RunDetectorAsync(_headerAnalyzer, httpContext, context, bus, ct);

            // IP analysis
            await RunDetectorAsync(_ipAnalyzer, httpContext, context, bus, ct);

            // Client-side fingerprint (if enabled)
            if (_clientSideAnalyzer != null && _options.ClientSide.Enabled)
                await RunDetectorAsync(_clientSideAnalyzer, httpContext, context, bus, ct);
            else
                bus.ReportCompletion("Client-Side Detector", DetectorCompletionStatus.Skipped);

            // === Stage 1: Behavioral (depends on stage 0) ===
            await RunDetectorAsync(_behavioralAnalyzer, httpContext, context, bus, ct);

            // === Stage 2: Meta-analysis (reads all prior signals) ===
            await RunDetectorAsync(_inconsistencyAnalyzer, httpContext, context, bus, ct);

            // === Stage 3: AI/ML (if enabled) ===
            if (_options.EnableLlmDetection)
            {
                if (_options.AiDetection.Provider == AiProvider.Heuristic && _heuristicAnalyzer != null)
                    await RunDetectorAsync(_heuristicAnalyzer, httpContext, context, bus, ct);
                // LLM detection now handled by background LlmClassificationCoordinator via plugin packages
                bus.TryPublish(BotSignalType.AiClassificationCompleted, "Orchestrator");
            }
            else
            {
                bus.ReportCompletion("Heuristic Detector", DetectorCompletionStatus.Skipped);
                bus.ReportCompletion("LLM Detector", DetectorCompletionStatus.Skipped);
            }

            // Wait for consensus (all detectors reported) with timeout
            var consensusReached = await bus.WaitForConsensusAsync(
                TimeSpan.FromMilliseconds(500), ct);

            if (!consensusReached)
                _logger.LogWarning("Consensus timeout. Pending: {Pending}",
                    string.Join(", ", bus.PendingDetectors));

            return await FinaliseAsync(context, bus, sw, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signal-driven detection failed");
            sw.Stop();
            _metrics?.RecordError("SignalDrivenDetection", ex.GetType().Name);

            return new BotDetectionResult
            {
                IsBot = false,
                ConfidenceScore = 0.0,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    private IEnumerable<string> GetExpectedDetectors()
    {
        var detectors = new List<string>(CoreDetectors);

        if (_clientSideAnalyzer != null && _options.ClientSide.Enabled)
            detectors.Add("Client-Side Detector");

        if (_options.EnableLlmDetection)
        {
            if (_options.AiDetection.Provider == AiProvider.Heuristic)
                detectors.Add("Heuristic Detector");
            else if (_options.AiDetection.Provider == AiProvider.Ollama)
                detectors.Add("LLM Detector");
        }

        return detectors;
    }

    private async Task<DetectorResult> RunDetectorAsync(
        IDetector detector,
        HttpContext httpContext,
        DetectionContext context,
        BotSignalBus bus,
        CancellationToken ct)
    {
        try
        {
            var result = await detector.DetectAsync(httpContext, ct);
            StoreResult(context, detector.Name, result);

            // Publish appropriate signal
            var signalType = GetSignalTypeForDetector(detector.Name);
            bus.TryPublish(signalType, detector.Name);

            // Report completion for consensus
            var status = DetectorCompletionStatus.Completed;

            // Check for early exit conditions
            if (result.BotType == BotType.VerifiedBot)
            {
                status = DetectorCompletionStatus.EarlyExit;
                bus.ReportCompletion(detector.Name, status, "Verified bot detected");
            }
            else
            {
                bus.ReportCompletion(detector.Name, status);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detector {Name} failed", detector.Name);
            bus.ReportCompletion(detector.Name, DetectorCompletionStatus.Failed, ex.Message);
            return new DetectorResult();
        }
    }

    private static BotSignalType GetSignalTypeForDetector(string detectorName)
    {
        return detectorName switch
        {
            "User-Agent Detector" => BotSignalType.UserAgentAnalyzed,
            "Header Detector" => BotSignalType.HeadersAnalyzed,
            "IP Detector" => BotSignalType.IpAnalyzed,
            "Client-Side Detector" => BotSignalType.ClientFingerprintReceived,
            "Behavioral Detector" => BotSignalType.BehaviourSampled,
            "Inconsistency Detector" => BotSignalType.InconsistencyUpdated,
            "ONNX Detector" or "LLM Detector" => BotSignalType.AiClassificationCompleted,
            _ => BotSignalType.DetectorComplete
        };
    }

    private async Task<BotDetectionResult> FinaliseAsync(
        DetectionContext context,
        BotSignalBus bus,
        Stopwatch sw,
        CancellationToken ct)
    {
        // Publish finalising signal - triggers risk assessment listener
        bus.TryPublish(BotSignalType.Finalising, "Orchestrator");

        // Process any pending signals (invokes listeners)
        await bus.ProcessSignalsAsync(context, ct);

        sw.Stop();

        // Build final result
        var result = BuildResult(context, sw.ElapsedMilliseconds);

        // Publish to learning bus for async processing
        PublishToLearningBus(context, result);

        return result;
    }

    private void PublishToLearningBus(DetectionContext context, BotDetectionResult result)
    {
        if (_learningBus == null)
            return;

        // Only publish high-confidence detections for learning
        if (result.ConfidenceScore >= InferenceTriggers.HighConfidenceThreshold)
        {
            var features = ExtractFeatures(context);

            _learningBus.TryPublish(new LearningEvent
            {
                Type = LearningEventType.HighConfidenceDetection,
                Source = "SignalDrivenDetectionService",
                Confidence = result.ConfidenceScore,
                Label = result.IsBot,
                Features = features,
                RequestId = context.HttpContext.TraceIdentifier,
                Metadata = new Dictionary<string, object>
                {
                    ["botType"] = result.BotType?.ToString() ?? "Unknown",
                    ["detectorCount"] = context.DetectorResults.Count,
                    ["processingTimeMs"] = result.ProcessingTimeMs
                }
            });

            _logger.LogDebug(
                "Published high-confidence detection to learning bus: {Confidence:F2}",
                result.ConfidenceScore);
        }
    }

    private Dictionary<string, double> ExtractFeatures(DetectionContext context)
    {
        var features = new Dictionary<string, double>();

        // Convert scores to features
        foreach (var (name, score) in context.Scores)
            features[$"score.{name.ToLowerInvariant().Replace(" ", "_")}"] = score;

        // Add numeric signals as features
        foreach (var key in context.SignalKeys)
        {
            var value = context.GetSignal<object>(key);
            switch (value)
            {
                case double d:
                    features[key] = d;
                    break;
                case bool b:
                    features[key] = b ? 1.0 : 0.0;
                    break;
                case int i:
                    features[key] = i;
                    break;
            }
        }

        return features;
    }

    private void StoreResult(DetectionContext context, string detectorName, DetectorResult result)
    {
        context.SetDetectorResult(detectorName, result);
        context.SetScore(detectorName, result.Confidence);
        context.AddReasons(result.Reasons);

        _logger.LogDebug("{Detector} completed: confidence={Confidence:F2}",
            detectorName, result.Confidence);
    }

    private BotDetectionResult BuildResult(DetectionContext context, long processingTimeMs)
    {
        var result = new BotDetectionResult
        {
            ProcessingTimeMs = processingTimeMs
        };

        var detectorResults = context.DetectorResults.Values.ToList();

        // Aggregate reasons
        result.Reasons.AddRange(context.Reasons);

        // Calculate confidence (max + agreement boost)
        var confidences = detectorResults.Select(r => r.Confidence).ToList();
        var maxConfidence = confidences.Any() ? confidences.Max() : 0.0;
        var suspiciousCount = confidences.Count(c => c > 0.3);
        var agreementBoost = suspiciousCount > 1 ? (suspiciousCount - 1) * 0.1 : 0.0;

        result.ConfidenceScore = Math.Min(maxConfidence + agreementBoost, 1.0);
        result.IsBot = result.ConfidenceScore >= _options.BotThreshold;

        // Determine bot type
        var botTypes = detectorResults
            .Where(r => r.BotType.HasValue && r.BotType != BotType.Unknown)
            .Select(r => r.BotType!.Value)
            .ToList();

        if (botTypes.Any())
        {
            if (botTypes.Contains(BotType.VerifiedBot))
            {
                result.BotType = BotType.VerifiedBot;
                result.IsBot = false;
                result.ConfidenceScore = 0.0;
            }
            else if (botTypes.Contains(BotType.MaliciousBot))
            {
                result.BotType = BotType.MaliciousBot;
            }
            else
            {
                result.BotType = botTypes.First();
            }
        }

        // Extract bot name
        var botName = detectorResults.FirstOrDefault(r => !string.IsNullOrEmpty(r.BotName))?.BotName;
        if (!string.IsNullOrEmpty(botName))
            result.BotName = botName;

        return result;
    }
}