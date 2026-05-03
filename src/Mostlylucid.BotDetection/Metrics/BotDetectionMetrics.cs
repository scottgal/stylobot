using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mostlylucid.BotDetection.Metrics;

/// <summary>
///     Provides metrics for bot detection operations.
///     Uses System.Diagnostics.Metrics for OpenTelemetry compatibility.
/// </summary>
public sealed class BotDetectionMetrics : IDisposable
{
    public const string MeterName = "Mostlylucid.BotDetection";
    private const int MaxRecentConfidences = 100;
    private readonly ObservableGauge<double> _averageConfidence;
    private readonly Counter<long> _botsDetected;
    private readonly ObservableGauge<int> _cachedCidrRangesCount;

    // Gauges (using ObservableGauge)
    private readonly ObservableGauge<int> _cachedPatternsCount;
    private readonly Histogram<int> _cacheFlushBatchSize;
    private readonly Histogram<double> _cacheFlushDuration;
    private readonly Counter<long> _cacheFlushErrors;
    private readonly Counter<long> _cacheFlushes;

    // WeightStore cache counters
    private readonly Counter<long> _cacheHits;
    private readonly Counter<long> _cacheMisses;
    private readonly Counter<long> _cacheWrites;
    private readonly Histogram<double> _cidrMatchDuration;
    private readonly object _confidenceLock = new();

    // Histograms
    private readonly Histogram<double> _detectionDuration;
    private readonly Counter<long> _detectorErrors;
    private readonly Counter<long> _humansDetected;

    private readonly Meter _meter;
    private readonly Histogram<double> _patternMatchDuration;
    private readonly Queue<double> _recentConfidences = new();

    // Counters
    private readonly Counter<long> _requestsTotal;
    private readonly ObservableGauge<int> _weightStoreCacheSize;
    private readonly ObservableGauge<int> _weightStorePendingWrites;
    private int _cidrCount;
    private int _currentCacheSize;
    private int _currentPendingWrites;

    // Internal state for gauges
    private int _patternCount;
    private double _recentAverageConfidence;

    public BotDetectionMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName, "1.0.0");

        // Request counters
        _requestsTotal = _meter.CreateCounter<long>(
            "botdetection.requests.total",
            "{request}",
            "Total number of bot detection requests processed");

        _botsDetected = _meter.CreateCounter<long>(
            "botdetection.bots.detected",
            "{bot}",
            "Number of requests classified as bots");

        _humansDetected = _meter.CreateCounter<long>(
            "botdetection.humans.detected",
            "{human}",
            "Number of requests classified as humans");

        _detectorErrors = _meter.CreateCounter<long>(
            "botdetection.errors.total",
            "{error}",
            "Total number of detector errors");

        // Duration histograms
        _detectionDuration = _meter.CreateHistogram<double>(
            "botdetection.detection.duration",
            "ms",
            "Time taken for complete bot detection");

        _patternMatchDuration = _meter.CreateHistogram<double>(
            "botdetection.pattern.match.duration",
            "ms",
            "Time taken for pattern matching");

        _cidrMatchDuration = _meter.CreateHistogram<double>(
            "botdetection.cidr.match.duration",
            "ms",
            "Time taken for CIDR range matching");

        // WeightStore cache metrics
        _cacheHits = _meter.CreateCounter<long>(
            "botdetection.weightstore.cache.hits",
            "{hit}",
            "Number of WeightStore cache hits");

        _cacheMisses = _meter.CreateCounter<long>(
            "botdetection.weightstore.cache.misses",
            "{miss}",
            "Number of WeightStore cache misses (DB lookups)");

        _cacheWrites = _meter.CreateCounter<long>(
            "botdetection.weightstore.cache.writes",
            "{write}",
            "Number of writes queued for persistence");

        _cacheFlushes = _meter.CreateCounter<long>(
            "botdetection.weightstore.flush.total",
            "{flush}",
            "Number of flush operations to SQLite");

        _cacheFlushErrors = _meter.CreateCounter<long>(
            "botdetection.weightstore.flush.errors",
            "{error}",
            "Number of failed flush operations");

        _cacheFlushDuration = _meter.CreateHistogram<double>(
            "botdetection.weightstore.flush.duration",
            "ms",
            "Time taken to flush pending writes to SQLite");

        _cacheFlushBatchSize = _meter.CreateHistogram<int>(
            "botdetection.weightstore.flush.batch_size",
            "{write}",
            "Number of writes per flush batch");

        // Observable gauges for cache state
        _cachedPatternsCount = _meter.CreateObservableGauge(
            "botdetection.cache.patterns.count",
            () => _patternCount,
            "{pattern}",
            "Number of compiled patterns in cache");

        _cachedCidrRangesCount = _meter.CreateObservableGauge(
            "botdetection.cache.cidr.count",
            () => _cidrCount,
            "{range}",
            "Number of parsed CIDR ranges in cache");

        _averageConfidence = _meter.CreateObservableGauge(
            "botdetection.confidence.average",
            () => _recentAverageConfidence,
            "1",
            "Average confidence score of recent detections");

        _weightStoreCacheSize = _meter.CreateObservableGauge(
            "botdetection.weightstore.cache.size",
            () => _currentCacheSize,
            "{entry}",
            "Current number of entries in WeightStore cache");

        _weightStorePendingWrites = _meter.CreateObservableGauge(
            "botdetection.weightstore.pending_writes",
            () => _currentPendingWrites,
            "{write}",
            "Number of writes pending flush to SQLite");
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    /// <summary>
    ///     Records a completed detection request.
    /// </summary>
    public void RecordDetection(double confidence, bool isBot, TimeSpan duration, string? detectorName = null)
    {
        var tags = detectorName != null
            ? new TagList { { "detector", detectorName } }
            : default;

        _requestsTotal.Add(1, tags);
        _detectionDuration.Record(duration.TotalMilliseconds, tags);

        if (isBot)
            _botsDetected.Add(1, tags);
        else
            _humansDetected.Add(1, tags);

        UpdateAverageConfidence(confidence);
    }

    /// <summary>
    ///     Records a pattern matching operation.
    /// </summary>
    public void RecordPatternMatch(TimeSpan duration, bool matched, string? patternType = null)
    {
        var tags = new TagList
        {
            { "matched", matched.ToString().ToLowerInvariant() },
            { "type", patternType ?? "unknown" }
        };

        _patternMatchDuration.Record(duration.TotalMilliseconds, tags);
    }

    /// <summary>
    ///     Records a CIDR matching operation.
    /// </summary>
    public void RecordCidrMatch(TimeSpan duration, bool matched, string? provider = null)
    {
        var tags = new TagList
        {
            { "matched", matched.ToString().ToLowerInvariant() },
            { "provider", provider ?? "unknown" }
        };

        _cidrMatchDuration.Record(duration.TotalMilliseconds, tags);
    }

    /// <summary>
    ///     Records a detector error.
    /// </summary>
    public void RecordError(string detectorName, string errorType)
    {
        var tags = new TagList
        {
            { "detector", detectorName },
            { "error_type", errorType }
        };

        _detectorErrors.Add(1, tags);
    }

    /// <summary>
    ///     Records a client-side browser fingerprint submission.
    /// </summary>
    public void RecordClientSideFingerprint(bool isHeadless, int integrityScore, string? automation)
    {
        var tags = new TagList
        {
            { "headless", isHeadless.ToString().ToLowerInvariant() },
            { "automation", automation ?? "none" }
        };

        _requestsTotal.Add(1, new TagList { { "detector", "ClientSide" } });

        if (isHeadless)
            _botsDetected.Add(1, tags);
        else
            _humansDetected.Add(1, tags);
    }

    /// <summary>
    ///     Updates the cached pattern count gauge.
    /// </summary>
    public void UpdatePatternCount(int count)
    {
        _patternCount = count;
    }

    /// <summary>
    ///     Updates the cached CIDR range count gauge.
    /// </summary>
    public void UpdateCidrCount(int count)
    {
        _cidrCount = count;
    }

    /// <summary>
    ///     Creates a timer scope that automatically records duration.
    /// </summary>
    public DetectionTimer StartDetectionTimer(string? detectorName = null)
    {
        return new DetectionTimer(this, detectorName);
    }

    private void UpdateAverageConfidence(double confidence)
    {
        lock (_confidenceLock)
        {
            _recentConfidences.Enqueue(confidence);
            while (_recentConfidences.Count > MaxRecentConfidences)
                _recentConfidences.Dequeue();

            _recentAverageConfidence = _recentConfidences.Average();
        }
    }

    #region WeightStore Cache Metrics

    /// <summary>
    ///     Records a cache hit (value found in memory).
    /// </summary>
    public void RecordCacheHit(string? signatureType = null)
    {
        var tags = signatureType != null
            ? new TagList { { "signature_type", signatureType } }
            : default;
        _cacheHits.Add(1, tags);
    }

    /// <summary>
    ///     Records a cache miss (required DB lookup).
    /// </summary>
    public void RecordCacheMiss(string? signatureType = null)
    {
        var tags = signatureType != null
            ? new TagList { { "signature_type", signatureType } }
            : default;
        _cacheMisses.Add(1, tags);
    }

    /// <summary>
    ///     Records a write queued for persistence.
    /// </summary>
    public void RecordCacheWrite(string? signatureType = null)
    {
        var tags = signatureType != null
            ? new TagList { { "signature_type", signatureType } }
            : default;
        _cacheWrites.Add(1, tags);
    }

    /// <summary>
    ///     Records a completed flush operation.
    /// </summary>
    public void RecordFlush(int batchSize, TimeSpan duration, bool success)
    {
        if (success)
        {
            _cacheFlushes.Add(1);
            _cacheFlushDuration.Record(duration.TotalMilliseconds);
            _cacheFlushBatchSize.Record(batchSize);
        }
        else
        {
            _cacheFlushErrors.Add(1);
        }
    }

    /// <summary>
    ///     Updates the current cache size gauge.
    /// </summary>
    public void UpdateWeightStoreCacheSize(int size)
    {
        _currentCacheSize = size;
    }

    /// <summary>
    ///     Updates the pending writes gauge.
    /// </summary>
    public void UpdatePendingWrites(int count)
    {
        _currentPendingWrites = count;
    }

    #endregion
}

/// <summary>
///     Timer scope for measuring detection duration.
/// </summary>
public readonly struct DetectionTimer : IDisposable
{
    private readonly BotDetectionMetrics _metrics;
    private readonly string? _detectorName;
    private readonly Stopwatch _stopwatch;

    public DetectionTimer(BotDetectionMetrics metrics, string? detectorName)
    {
        _metrics = metrics;
        _detectorName = detectorName;
        _stopwatch = Stopwatch.StartNew();
    }

    /// <summary>
    ///     Stops the timer and records the detection.
    /// </summary>
    public void Complete(double confidence, bool isBot)
    {
        _stopwatch.Stop();
        _metrics.RecordDetection(confidence, isBot, _stopwatch.Elapsed, _detectorName);
    }

    public void Dispose()
    {
        // If not explicitly completed, just stop the timer
        _stopwatch.Stop();
    }
}

/// <summary>
///     Extension methods for adding metrics to DI.
/// </summary>
public static class MetricsExtensions
{
    /// <summary>
    ///     Gets the elapsed time as a TimeSpan.
    /// </summary>
    public static TimeSpan GetElapsed(this Stopwatch stopwatch)
    {
        return stopwatch.Elapsed;
    }
}