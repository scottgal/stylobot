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

        // Track signature hit counts (single source of truth — AddSignatureAsync reads only)
        if (!string.IsNullOrEmpty(detection.PrimarySignature))
            _signatureHitCounts.AddOrUpdate(detection.PrimarySignature, 1, (_, count) => count + 1);

        // Trim if over limit
        while (_detections.Count > _maxEvents) _detections.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
    {
        // Read hit count from detection tracking (don't increment again — that would double-count)
        var hitCount = _signatureHitCounts.GetOrAdd(signature.PrimarySignature, 1);

        var updatedSignature = signature with { HitCount = hitCount };
        _signatures.Enqueue(updatedSignature);

        // Trim if over limit
        while (_signatures.Count > _maxEvents) _signatures.TryDequeue(out _);

        return Task.FromResult(updatedSignature);
    }

    public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null)
    {
        IEnumerable<DashboardDetectionEvent> query = _detections;

        if (filter != null)
        {
            if (filter.StartTime.HasValue)
                query = query.Where(d => d.Timestamp >= filter.StartTime.Value);

            if (filter.EndTime.HasValue)
                query = query.Where(d => d.Timestamp <= filter.EndTime.Value);

            if (filter.RiskBands?.Any() == true)
                query = query.Where(d => filter.RiskBands.Contains(d.RiskBand));

            if (filter.IsBot.HasValue)
                query = query.Where(d => d.IsBot == filter.IsBot.Value);

            if (!string.IsNullOrEmpty(filter.PathContains))
                query = query.Where(d => d.Path.Contains(filter.PathContains, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(filter.SignatureId))
                query = query.Where(d => d.PrimarySignature == filter.SignatureId);

            if (filter.HighRiskOnly)
                query = query.Where(d => d.RiskBand is "High" or "VeryHigh");
        }

        // Order first, then apply offset/limit for correct pagination
        var orderedQuery = query.OrderByDescending(d => d.Timestamp);

        IEnumerable<DashboardDetectionEvent> result = orderedQuery;
        if (filter?.Offset > 0)
            result = result.Skip(filter.Offset.Value);

        if (filter?.Limit.HasValue == true)
            result = result.Take(filter.Limit.Value);

        return Task.FromResult(result.ToList());
    }

    public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
    {
        IEnumerable<DashboardSignatureEvent> query = _signatures.OrderByDescending(s => s.Timestamp);
        if (isBot.HasValue) query = query.Where(s => s.IsKnownBot == isBot.Value);
        if (offset > 0) query = query.Skip(offset);
        return Task.FromResult(query.Take(limit).ToList());
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
        var lastProcessingTime = detections.Count > 0
            ? detections[^1].ProcessingTimeMs
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
            LastProcessingTimeMs = lastProcessingTime,
            UniqueSignatures = _signatureHitCounts.Count
        };

        return Task.FromResult(summary);
    }

    public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10)
    {
        // Group signatures by PrimarySignature, keep the latest entry per signature,
        // filter to bots only, and return top N by hit count.
        var topBots = _signatures
            .Where(s => s.IsKnownBot)
            .GroupBy(s => s.PrimarySignature)
            .Select(g =>
            {
                var latest = g.OrderByDescending(s => s.Timestamp).First();
                // Use the hit count from our authoritative tracking dictionary
                var hitCount = _signatureHitCounts.GetValueOrDefault(latest.PrimarySignature, latest.HitCount);
                return new DashboardTopBotEntry
                {
                    PrimarySignature = latest.PrimarySignature,
                    HitCount = hitCount,
                    BotName = latest.BotName,
                    BotType = latest.BotType,
                    RiskBand = latest.RiskBand,
                    BotProbability = latest.BotProbability ?? 0,
                    Confidence = latest.Confidence ?? 0,
                    Action = latest.Action,
                    CountryCode = null, // Not tracked on signatures
                    ProcessingTimeMs = latest.ProcessingTimeMs ?? 0,
                    TopReasons = latest.TopReasons,
                    LastSeen = latest.Timestamp,
                    Narrative = latest.Narrative,
                    Description = latest.Description,
                    IsKnownBot = latest.IsKnownBot
                };
            })
            .OrderByDescending(b => b.HitCount)
            .Take(count)
            .ToList();

        return Task.FromResult(topBots);
    }

    public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20)
    {
        var countryStats = _detections
            .Where(d => !string.IsNullOrEmpty(d.CountryCode)
                        && d.CountryCode != "XX"
                        && d.CountryCode != "LOCAL")
            .GroupBy(d => d.CountryCode!.ToUpperInvariant())
            .Select(g => new DashboardCountryStats
            {
                CountryCode = g.Key,
                CountryName = g.Key, // No name lookup in-memory
                TotalCount = g.Count(),
                BotCount = g.Count(d => d.IsBot),
                BotRate = g.Count() > 0
                    ? Math.Round((double)g.Count(d => d.IsBot) / g.Count(), 3)
                    : 0
            })
            .OrderByDescending(c => c.TotalCount)
            .Take(count)
            .ToList();

        return Task.FromResult(countryStats);
    }

    public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize)
    {
        // Guard against infinite loop from zero/negative bucket size
        if (bucketSize <= TimeSpan.Zero)
            return Task.FromResult(new List<DashboardTimeSeriesPoint>());

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