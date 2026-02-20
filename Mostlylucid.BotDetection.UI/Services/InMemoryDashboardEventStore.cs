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
    private readonly ConcurrentQueue<DashboardSignatureEvent> _signatures = new();

    public InMemoryDashboardEventStore(StyloBotDashboardOptions options)
    {
        _maxEvents = options.MaxEventsInMemory;
    }

    public Task AddDetectionAsync(DashboardDetectionEvent detection)
    {
        _detections.Enqueue(detection);

        // Trim if over limit
        while (_detections.Count > _maxEvents) _detections.TryDequeue(out _);

        return Task.CompletedTask;
    }

    public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
    {
        _signatures.Enqueue(signature);

        // Trim if over limit
        while (_signatures.Count > _maxEvents) _signatures.TryDequeue(out _);

        return Task.FromResult(signature);
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
            UniqueSignatures = detections
                .Where(d => !string.IsNullOrEmpty(d.PrimarySignature))
                .Select(d => d.PrimarySignature)
                .Distinct()
                .Count()
        };

        return Task.FromResult(summary);
    }

    public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null)
    {
        // Group detections by signature — works even when signatures aren't stored
        // (e.g. upstream-trusted mode where only detections are recorded).
        IEnumerable<DashboardDetectionEvent> source = _detections;
        if (startTime.HasValue) source = source.Where(d => d.Timestamp >= startTime.Value);
        if (endTime.HasValue) source = source.Where(d => d.Timestamp <= endTime.Value);
        var topBots = source
            .Where(d => d.IsBot && !string.IsNullOrEmpty(d.PrimarySignature))
            .GroupBy(d => d.PrimarySignature!)
            .Select(g =>
            {
                var latest = g.OrderByDescending(d => d.Timestamp).First();
                return new DashboardTopBotEntry
                {
                    PrimarySignature = g.Key,
                    HitCount = g.Count(),
                    BotName = latest.BotName,
                    BotType = latest.BotType,
                    RiskBand = latest.RiskBand,
                    BotProbability = latest.BotProbability,
                    Confidence = latest.Confidence,
                    Action = latest.Action,
                    CountryCode = latest.CountryCode,
                    ProcessingTimeMs = latest.ProcessingTimeMs,
                    TopReasons = latest.TopReasons,
                    LastSeen = latest.Timestamp,
                    Narrative = latest.Narrative,
                    Description = latest.Description,
                    IsKnownBot = true
                };
            })
            .OrderByDescending(b => b.HitCount)
            .Take(count)
            .ToList();

        return Task.FromResult(topBots);
    }

    public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        IEnumerable<DashboardDetectionEvent> source = _detections;
        if (startTime.HasValue) source = source.Where(d => d.Timestamp >= startTime.Value);
        if (endTime.HasValue) source = source.Where(d => d.Timestamp <= endTime.Value);

        var countryStats = source
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

    public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
    {
        var code = countryCode.ToUpperInvariant();
        IEnumerable<DashboardDetectionEvent> source = _detections;
        if (startTime.HasValue) source = source.Where(d => d.Timestamp >= startTime.Value);
        if (endTime.HasValue) source = source.Where(d => d.Timestamp <= endTime.Value);

        var countryDetections = source
            .Where(d => string.Equals(d.CountryCode, code, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (countryDetections.Count == 0)
            return Task.FromResult<DashboardCountryDetail?>(null);

        var totalCount = countryDetections.Count;
        var botCount = countryDetections.Count(d => d.IsBot);

        var topBotTypes = countryDetections
            .Where(d => d.BotType != null)
            .GroupBy(d => d.BotType!)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        var topActions = countryDetections
            .Where(d => d.Action != null)
            .GroupBy(d => d.Action!)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .ToDictionary(g => g.Key, g => g.Count());

        var topBots = countryDetections
            .Where(d => d.IsBot && !string.IsNullOrEmpty(d.PrimarySignature))
            .GroupBy(d => d.PrimarySignature!)
            .Select(g =>
            {
                var latest = g.OrderByDescending(d => d.Timestamp).First();
                return new DashboardTopBotEntry
                {
                    PrimarySignature = g.Key,
                    HitCount = g.Count(),
                    BotName = latest.BotName,
                    BotType = latest.BotType,
                    RiskBand = latest.RiskBand,
                    BotProbability = latest.BotProbability,
                    Confidence = latest.Confidence,
                    Action = latest.Action,
                    CountryCode = latest.CountryCode,
                    ProcessingTimeMs = latest.ProcessingTimeMs,
                    TopReasons = latest.TopReasons,
                    LastSeen = latest.Timestamp,
                    IsKnownBot = true
                };
            })
            .OrderByDescending(b => b.HitCount)
            .Take(10)
            .ToList();

        var detail = new DashboardCountryDetail
        {
            CountryCode = code,
            CountryName = code,
            TotalCount = totalCount,
            BotCount = botCount,
            BotRate = totalCount > 0 ? Math.Round((double)botCount / totalCount, 3) : 0,
            TopBotTypes = topBotTypes,
            TopActions = topActions,
            TopBots = topBots
        };

        return Task.FromResult<DashboardCountryDetail?>(detail);
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