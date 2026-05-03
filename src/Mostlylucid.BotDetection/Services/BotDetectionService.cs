using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Telemetry;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Main bot detection service that orchestrates multiple detection strategies
/// </summary>
public class BotDetectionService : IBotDetectionService
{
    private readonly IMemoryCache _cache;
    private readonly IEnumerable<IDetector> _detectors;
    private readonly ILogger<BotDetectionService> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly BotDetectionStatistics _statistics = new();
    private readonly object _statsLock = new();

    public BotDetectionService(
        ILogger<BotDetectionService> logger,
        IOptions<BotDetectionOptions> options,
        IMemoryCache cache,
        IEnumerable<IDetector> detectors,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _cache = cache;
        _detectors = detectors;
        _metrics = metrics;
    }

    public async Task<BotDetectionResult> DetectAsync(HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Request.Headers.UserAgent.ToString();

        using var activity = BotDetectionTelemetry.StartDetectActivity(clientIp, userAgent);

        try
        {
            // Check cache first
            var cacheKey = BuildCacheKey(context);
            if (_cache.TryGetValue<BotDetectionResult>(cacheKey, out var cachedResult) && cachedResult != null)
            {
                _logger.LogDebug("Returning cached bot detection result");
                activity?.SetTag("mostlylucid.botdetection.cache_hit", true);
                BotDetectionTelemetry.RecordResult(activity, cachedResult);
                return cachedResult;
            }

            activity?.SetTag("mostlylucid.botdetection.cache_hit", false);

            var result = new BotDetectionResult();

            // Create shared detection context for cross-detector communication
            var detectionContext = new DetectionContext
            {
                HttpContext = context,
                CancellationToken = cancellationToken
            };

            // Run detectors in staged order - each stage completes before the next begins
            await RunDetectorsInStagesAsync(detectionContext);

            // Combine results using weighted scoring
            result = CombineResults(detectionContext);
            sw.Stop();
            result.ProcessingTimeMs = sw.ElapsedMilliseconds;

            // Cache result
            _cache.Set(cacheKey, result, TimeSpan.FromSeconds(_options.CacheDurationSeconds));

            // Update statistics
            UpdateStatistics(result);

            // Record metrics
            _metrics?.RecordDetection(result.ConfidenceScore, result.IsBot, sw.Elapsed, "Combined");

            _logger.LogInformation(
                "Bot detection complete: IsBot={IsBot}, Confidence={Confidence:F2}, Time={Time}ms",
                result.IsBot, result.ConfidenceScore, result.ProcessingTimeMs);

            activity?.SetTag("mostlylucid.botdetection.detector_count", _detectors.Count());
            BotDetectionTelemetry.RecordResult(activity, result);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bot detection failed");
            sw.Stop();
            _metrics?.RecordError("BotDetectionService", ex.GetType().Name);
            BotDetectionTelemetry.RecordException(activity, ex);
            return new BotDetectionResult
            {
                IsBot = false,
                ConfidenceScore = 0.0,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
    }

    public BotDetectionStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new BotDetectionStatistics
            {
                TotalRequests = _statistics.TotalRequests,
                BotsDetected = _statistics.BotsDetected,
                VerifiedBots = _statistics.VerifiedBots,
                MaliciousBots = _statistics.MaliciousBots,
                AverageProcessingTimeMs = _statistics.AverageProcessingTimeMs,
                BotTypeBreakdown = new Dictionary<string, int>(_statistics.BotTypeBreakdown)
            };
        }
    }

    /// <summary>
    ///     Run detectors in staged order. Each stage completes before the next begins.
    ///     Detectors within the same stage run in parallel.
    /// </summary>
    private async Task RunDetectorsInStagesAsync(DetectionContext detectionContext)
    {
        // Group detectors by stage
        var detectorsByStage = _detectors
            .GroupBy(d => d.Stage)
            .OrderBy(g => (int)g.Key)
            .ToList();

        foreach (var stageGroup in detectorsByStage)
        {
            var stage = stageGroup.Key;
            var detectorsInStage = stageGroup.ToList();

            _logger.LogDebug("Running {Count} detectors in stage {Stage}",
                detectorsInStage.Count, stage);

            // Run all detectors in this stage in parallel
            var tasks = detectorsInStage.Select(async detector =>
            {
                try
                {
                    var detectorResult = await detector.DetectAsync(detectionContext);

                    // Store result in context for other detectors to read
                    detectionContext.SetDetectorResult(detector.Name, detectorResult);
                    detectionContext.SetScore(detector.Name, detectorResult.Confidence);
                    detectionContext.AddReasons(detectorResult.Reasons);

                    _logger.LogDebug("{Detector} (stage {Stage}) confidence: {Confidence:F2}",
                        detector.Name, stage, detectorResult.Confidence);

                    return (detector.Name, Result: (DetectorResult?)detectorResult, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Detector {Detector} failed in stage {Stage}",
                        detector.Name, stage);
                    _metrics?.RecordError(detector.Name, ex.GetType().Name);
                    return (detector.Name, Result: (DetectorResult?)null, Error: (Exception?)ex);
                }
            });

            // Wait for all detectors in this stage to complete before moving to next stage
            var results = await Task.WhenAll(tasks);

            // Log stage completion
            var successful = results.Count(r => r.Result != null);
            var failed = results.Count(r => r.Error != null);
            _logger.LogDebug("Stage {Stage} complete: {Successful} succeeded, {Failed} failed",
                stage, successful, failed);
        }
    }

    private BotDetectionResult CombineResults(DetectionContext detectionContext)
    {
        var result = new BotDetectionResult();
        var detectorResults = detectionContext.DetectorResults.Values.ToList();

        // Use accumulated reasons from context
        result.Reasons.AddRange(detectionContext.Reasons);

        // Calculate weighted confidence score
        // Strategy: Take maximum confidence, but boost if multiple detectors agree
        var confidences = detectorResults.Select(r => r.Confidence).ToList();
        var maxConfidence = confidences.Any() ? confidences.Max() : 0.0;

        // If multiple detectors show suspicion, boost the score
        var suspiciousDetectors = confidences.Count(c => c > 0.3);
        var agreementBoost = suspiciousDetectors > 1 ? (suspiciousDetectors - 1) * 0.1 : 0.0;

        result.ConfidenceScore = Math.Min(maxConfidence + agreementBoost, 1.0);
        result.IsBot = result.ConfidenceScore >= _options.BotThreshold;

        // Determine bot type (prefer specific types over unknown)
        var botTypes = detectorResults
            .Where(r => r.BotType.HasValue && r.BotType != BotType.Unknown)
            .Select(r => r.BotType!.Value)
            .ToList();

        if (botTypes.Any())
        {
            // Prioritize verified bots, then malicious, then others
            if (botTypes.Contains(BotType.VerifiedBot))
            {
                result.BotType = BotType.VerifiedBot;
                result.IsBot = false; // Verified bots are allowed
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

        // Extract bot name if identified
        var botName = detectorResults.FirstOrDefault(r => !string.IsNullOrEmpty(r.BotName))?.BotName;
        if (!string.IsNullOrEmpty(botName)) result.BotName = botName;

        // Store learnings in HttpContext for later persistence
        if (detectionContext.Learnings.Any())
            detectionContext.HttpContext.Items["BotDetection.Learnings"] = detectionContext.Learnings;

        return result;
    }

    private string BuildCacheKey(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Include key headers that might affect detection
        var accept = context.Request.Headers.Accept.ToString();
        var acceptLanguage = context.Request.Headers.AcceptLanguage.ToString();

        // Use XxHash64 for stable, fast hashing (GetHashCode() is not stable across app runs)
        var input = $"{ip}|{userAgent}|{accept}|{acceptLanguage}";
        var hash = ComputeStableHash(input);
        return $"bot_detect_{hash}";
    }

    private static string ComputeStableHash(string input)
        => PatternNormalization.ComputeHash(input);

    private void UpdateStatistics(BotDetectionResult result)
    {
        lock (_statsLock)
        {
            _statistics.TotalRequests++;

            if (result.IsBot)
            {
                _statistics.BotsDetected++;

                if (result.BotType == BotType.VerifiedBot)
                    _statistics.VerifiedBots++;

                if (result.BotType == BotType.MaliciousBot)
                    _statistics.MaliciousBots++;

                if (result.BotType.HasValue)
                {
                    var typeName = result.BotType.Value.ToString();
                    _statistics.BotTypeBreakdown.TryGetValue(typeName, out var count);
                    _statistics.BotTypeBreakdown[typeName] = count + 1;
                }
            }

            // Update average processing time (rolling average)
            if (_statistics.TotalRequests == 1)
                _statistics.AverageProcessingTimeMs = result.ProcessingTimeMs;
            else
                _statistics.AverageProcessingTimeMs =
                    (_statistics.AverageProcessingTimeMs * (_statistics.TotalRequests - 1) + result.ProcessingTimeMs)
                    / _statistics.TotalRequests;
        }
    }
}