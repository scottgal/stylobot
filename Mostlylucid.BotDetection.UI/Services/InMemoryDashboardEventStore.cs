using System.Collections.Concurrent;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     In-memory storage for dashboard events.
///     Thread-safe, circular buffer with configurable max size.
/// </summary>
public class InMemoryDashboardEventStore : IDashboardEventStore
{
    private readonly ConcurrentQueue<DashboardDetectionEvent> _detections = new();
    private readonly int _maxEvents;
    private readonly ConcurrentDictionary<string, int> _signatureHitCounts = new();
    private readonly ConcurrentQueue<DashboardSignatureEvent> _signatures = new();
    private int _totalDetections;

    public InMemoryDashboardEventStore(StyloBotDashboardOptions options)
    {
        _maxEvents = options.MaxEventsInMemory;
    }

    public Task AddDetectionAsync(DashboardDetectionEvent detection)
    {
        _detections.Enqueue(detection);
        Interlocked.Increment(ref _totalDetections);

        // Track signature hit counts
        if (!string.IsNullOrEmpty(detection.PrimarySignature))
            _signatureHitCounts.AddOrUpdate(detection.PrimarySignature, 1, (_, count) => count + 1);

        // Trim if over limit
        while (_detections.Count > _maxEvents) _detections.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task AddSignatureAsync(DashboardSignatureEvent signature)
    {
        _signatures.Enqueue(signature);

        // Trim if over limit
        while (_signatures.Count > _maxEvents) _signatures.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null)
    {
        var detections = _detections.ToList();

        if (filter != null)
        {
            if (filter.StartTime.HasValue)
                detections = detections.Where(d => d.Timestamp >= filter.StartTime.Value).ToList();

            if (filter.EndTime.HasValue)
                detections = detections.Where(d => d.Timestamp <= filter.EndTime.Value).ToList();

            if (filter.RiskBands?.Any() == true)
                detections = detections.Where(d => filter.RiskBands.Contains(d.RiskBand)).ToList();

            if (filter.IsBot.HasValue)
                detections = detections.Where(d => d.IsBot == filter.IsBot.Value).ToList();

            if (!string.IsNullOrEmpty(filter.PathContains))
                detections = detections
                    .Where(d => d.Path.Contains(filter.PathContains, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrEmpty(filter.SignatureId))
                detections = detections.Where(d => d.PrimarySignature == filter.SignatureId).ToList();

            if (filter.HighRiskOnly)
                detections = detections.Where(d => d.RiskBand is "High" or "VeryHigh").ToList();

            // Apply offset and limit
            if (filter.Offset > 0)
                detections = detections.Skip(filter.Offset.Value).ToList();

            if (filter.Limit.HasValue)
                detections = detections.Take(filter.Limit.Value).ToList();
        }

        return Task.FromResult(detections.OrderByDescending(d => d.Timestamp).ToList());
    }

    public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100)
    {
        return Task.FromResult(_signatures
            .OrderByDescending(s => s.Timestamp)
            .Take(limit)
            .ToList());
    }

    public Task<DashboardSummary> GetSummaryAsync()
    {
        var detections = _detections.ToList();
        var now = DateTime.UtcNow;

        var botCount = detections.Count(d => d.IsBot);
        var humanCount = detections.Count(d => !d.IsBot && d.Confidence >= 0.5);
        var uncertainCount = detections.Count(d => !d.IsBot && d.Confidence < 0.5);

        var riskBands = detections
            .GroupBy(d => d.RiskBand)
            .ToDictionary(g => g.Key, g => g.Count());

        var topBotTypes = detections
            .Where(d => d.BotType != null)
            .GroupBy(d => d.BotType!)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToDictionary(g => g.Key, g => g.Count());

        var topActions = detections
            .Where(d => d.Action != null)
            .GroupBy(d => d.Action!)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToDictionary(g => g.Key, g => g.Count());

        var avgProcessingTime = detections.Any()
            ? detections.Average(d => d.ProcessingTimeMs)
            : 0;

        var summary = new DashboardSummary
        {
            Timestamp = now,
            TotalRequests = detections.Count,
            BotRequests = botCount,
            HumanRequests = humanCount,
            UncertainRequests = uncertainCount,
            RiskBandCounts = riskBands,
            TopBotTypes = topBotTypes,
            TopActions = topActions,
            AverageProcessingTimeMs = avgProcessingTime,
            UniqueSignatures = _signatureHitCounts.Count
        };

        return Task.FromResult(summary);
    }

    public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize)
    {
        var detections = _detections
            .Where(d => d.Timestamp >= startTime && d.Timestamp <= endTime)
            .ToList();

        var buckets = new List<DashboardTimeSeriesPoint>();
        var currentBucket = startTime;

        while (currentBucket < endTime)
        {
            var nextBucket = currentBucket.Add(bucketSize);
            var bucketDetections = detections
                .Where(d => d.Timestamp >= currentBucket && d.Timestamp < nextBucket)
                .ToList();

            var point = new DashboardTimeSeriesPoint
            {
                Timestamp = currentBucket,
                BotCount = bucketDetections.Count(d => d.IsBot),
                HumanCount = bucketDetections.Count(d => !d.IsBot),
                TotalCount = bucketDetections.Count,
                RiskBandCounts = bucketDetections
                    .GroupBy(d => d.RiskBand)
                    .ToDictionary(g => g.Key, g => g.Count())
            };

            buckets.Add(point);
            currentBucket = nextBucket;
        }

        return Task.FromResult(buckets);
    }
}