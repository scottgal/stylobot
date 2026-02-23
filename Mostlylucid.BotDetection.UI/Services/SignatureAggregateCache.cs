using System.Collections.Concurrent;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Write-through LFU cache that maintains per-signature aggregates,
///     updated on every detection. Single source of truth for top-bots data.
///     <para>
///     Thread safety: all mutations to <see cref="SignatureAggregate"/> are guarded
///     by the entry's <see cref="SignatureAggregate.SyncRoot"/> lock. The sorted index
///     is rebuilt lazily under <see cref="_sortLock"/> with double-checked locking.
///     </para>
///     <para>
///     MaxEntries is capped at 200 by default. Eviction scans are O(n) but batched
///     to amortize cost — eviction only triggers when 10% over capacity.
///     </para>
/// </summary>
public sealed class SignatureAggregateCache
{
    private readonly ConcurrentDictionary<string, SignatureAggregate> _entries = new();
    private readonly object _sortLock = new();
    private IReadOnlyList<DashboardTopBotEntry>? _sortedCache;
    private volatile bool _sortDirty = true;
    private long _updateCounter;

    /// <summary>Maximum entries before LFU eviction kicks in.</summary>
    public int MaxEntries { get; init; } = 200;

    /// <summary>Number of score history points to keep per signature (for sparklines).</summary>
    public int ScoreHistorySize { get; init; } = 20;

    /// <summary>Age access counts every N updates to prevent LFU starvation.</summary>
    private const int AccessCountAgingInterval = 500;

    /// <summary>Current number of tracked signatures.</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Update cache from a new detection event (write-through).
    ///     Called on every detection from DetectionBroadcastMiddleware.
    /// </summary>
    public void UpdateFromDetection(DashboardDetectionEvent detection)
    {
        if (string.IsNullOrEmpty(detection.PrimarySignature)) return;

        _entries.AddOrUpdate(
            detection.PrimarySignature,
            _ => CreateNew(detection),
            (_, existing) => Update(existing, detection));

        // Batch eviction: only trigger when 10% over capacity to amortize O(n) scan cost
        var overage = _entries.Count - MaxEntries;
        if (overage > MaxEntries / 10)
            EvictLfuBatch(overage);

        _sortDirty = true;

        // Periodically age access counts to prevent LFU starvation
        if (Interlocked.Increment(ref _updateCounter) % AccessCountAgingInterval == 0)
            AgeAccessCounts();
    }

    /// <summary>
    ///     Apply an LLM-generated bot name and description to a cached signature.
    ///     Called by <see cref="LlmResultSignalRCallback"/> when background LLM naming completes.
    /// </summary>
    public void ApplyBotName(string signature, string name, string? description = null)
    {
        if (!_entries.TryGetValue(signature, out var agg)) return;

        lock (agg.SyncRoot)
        {
            agg.BotName = name;
            if (description != null)
                agg.Description = description;
        }

        _sortDirty = true;
    }

    /// <summary>
    ///     Get paged, sorted top bots list.
    /// </summary>
    public List<DashboardTopBotEntry> GetTopBots(
        int page = 1,
        int pageSize = 25,
        string? sortBy = null,
        string? filterCountry = null)
    {
        var sorted = GetOrRebuildSortedList();

        IEnumerable<DashboardTopBotEntry> query = sorted;

        if (!string.IsNullOrEmpty(filterCountry))
            query = query.Where(b =>
                string.Equals(b.CountryCode, filterCountry, StringComparison.OrdinalIgnoreCase));

        query = (sortBy?.ToLowerInvariant()) switch
        {
            "name" => query.OrderBy(b => b.BotName ?? b.PrimarySignature),
            "lastseen" => query.OrderByDescending(b => b.LastSeen),
            "country" => query.OrderBy(b => b.CountryCode ?? "ZZ"),
            "probability" => query.OrderByDescending(b => b.BotProbability),
            _ => query // already sorted by hits desc
        };

        return query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>
    ///     Get sparkline score history for a specific signature.
    /// </summary>
    public List<double>? GetSparkline(string signature)
    {
        if (!_entries.TryGetValue(signature, out var agg))
            return null;

        lock (agg.SyncRoot)
        {
            return agg.ScoreHistory.ToList();
        }
    }

    /// <summary>
    ///     Try to get aggregate data for a specific signature.
    /// </summary>
    public bool TryGet(string signature, out SignatureAggregate? aggregate)
    {
        if (_entries.TryGetValue(signature, out var agg))
        {
            Interlocked.Increment(ref agg.AccessCount);
            aggregate = agg;
            return true;
        }

        aggregate = null;
        return false;
    }

    /// <summary>
    ///     Seed cache from event store data on startup.
    /// </summary>
    public void SeedFromTopBots(IEnumerable<DashboardTopBotEntry> topBots)
    {
        foreach (var bot in topBots)
        {
            _entries.TryAdd(bot.PrimarySignature, new SignatureAggregate
            {
                HitCount = bot.HitCount,
                BotName = bot.BotName,
                BotType = bot.BotType,
                RiskBand = bot.RiskBand,
                BotProbability = bot.BotProbability,
                Confidence = bot.Confidence,
                Action = bot.Action,
                CountryCode = bot.CountryCode,
                ProcessingTimeMs = bot.ProcessingTimeMs,
                TopReasons = bot.TopReasons,
                LastSeen = bot.LastSeen,
                Narrative = bot.Narrative,
                Description = bot.Description,
                IsBot = bot.IsKnownBot,
                ThreatScore = bot.ThreatScore,
                ThreatBand = bot.ThreatBand,
            });
        }

        _sortDirty = true;
    }

    // ─── Internal ────────────────────────────────────────────────────────

    private SignatureAggregate CreateNew(DashboardDetectionEvent detection)
    {
        var agg = new SignatureAggregate
        {
            HitCount = 1,
            BotName = detection.BotName,
            BotType = detection.BotType,
            RiskBand = detection.RiskBand,
            BotProbability = detection.BotProbability,
            Confidence = detection.Confidence,
            Action = detection.Action,
            CountryCode = detection.CountryCode,
            ProcessingTimeMs = detection.ProcessingTimeMs,
            TopReasons = detection.TopReasons,
            LastSeen = detection.Timestamp,
            Narrative = detection.Narrative,
            Description = detection.Description,
            IsBot = detection.IsBot,
            ThreatScore = detection.ThreatScore,
            ThreatBand = detection.ThreatBand,
        };

        // No lock needed — object is not yet visible to other threads
        agg.ScoreHistory.AddLast(detection.BotProbability);

        return agg;
    }

    /// <summary>
    ///     Update an existing aggregate under lock to prevent data races.
    ///     ConcurrentDictionary.AddOrUpdate may retry the factory, but the lock
    ///     ensures only one thread mutates the aggregate at a time.
    /// </summary>
    private SignatureAggregate Update(SignatureAggregate existing, DashboardDetectionEvent detection)
    {
        lock (existing.SyncRoot)
        {
            existing.HitCount++;
            existing.BotName = detection.BotName ?? existing.BotName;
            existing.BotType = detection.BotType ?? existing.BotType;
            existing.RiskBand = detection.RiskBand;
            existing.BotProbability = detection.BotProbability;
            existing.Confidence = detection.Confidence;
            existing.Action = detection.Action ?? existing.Action;
            existing.CountryCode = detection.CountryCode ?? existing.CountryCode;
            existing.ProcessingTimeMs = detection.ProcessingTimeMs;
            existing.TopReasons = detection.TopReasons ?? existing.TopReasons;
            existing.LastSeen = detection.Timestamp;
            existing.Narrative = detection.Narrative ?? existing.Narrative;
            existing.Description = detection.Description ?? existing.Description;
            existing.IsBot = detection.IsBot;
            existing.ThreatScore = detection.ThreatScore ?? existing.ThreatScore;
            existing.ThreatBand = detection.ThreatBand ?? existing.ThreatBand;

            existing.ScoreHistory.AddLast(detection.BotProbability);
            while (existing.ScoreHistory.Count > ScoreHistorySize)
                existing.ScoreHistory.RemoveFirst();
        }

        return existing;
    }

    private IReadOnlyList<DashboardTopBotEntry> GetOrRebuildSortedList()
    {
        // Fast path — double-checked locking. Benign staleness is acceptable for a dashboard.
        if (!_sortDirty && _sortedCache != null)
            return _sortedCache;

        lock (_sortLock)
        {
            if (!_sortDirty && _sortedCache != null)
                return _sortedCache;

            _sortedCache = _entries
                .Where(kvp => kvp.Value.IsBot)
                .Select(kvp => ToEntry(kvp.Key, kvp.Value))
                .OrderByDescending(b => b.HitCount)
                .ToList()
                .AsReadOnly();

            _sortDirty = false;
            return _sortedCache;
        }
    }

    private static DashboardTopBotEntry ToEntry(string signature, SignatureAggregate agg)
    {
        lock (agg.SyncRoot)
        {
            return new DashboardTopBotEntry
            {
                PrimarySignature = signature,
                HitCount = agg.HitCount,
                BotName = agg.BotName,
                BotType = agg.BotType,
                RiskBand = agg.RiskBand,
                BotProbability = agg.BotProbability,
                Confidence = agg.Confidence,
                Action = agg.Action,
                CountryCode = agg.CountryCode,
                ProcessingTimeMs = agg.ProcessingTimeMs,
                TopReasons = agg.TopReasons,
                LastSeen = agg.LastSeen,
                Narrative = agg.Narrative,
                Description = agg.Description,
                IsKnownBot = agg.IsBot,
                ThreatScore = agg.ThreatScore,
                ThreatBand = agg.ThreatBand,
            };
        }
    }

    /// <summary>
    ///     Batch eviction: remove the N entries with the lowest AccessCount,
    ///     skipping hot entries (high HitCount) unless all entries are hot.
    /// </summary>
    private void EvictLfuBatch(int count)
    {
        var hotThreshold = 10;
        var candidates = _entries
            .Where(kvp => kvp.Value.HitCount <= hotThreshold)
            .OrderBy(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
            .Take(count)
            .Select(kvp => kvp.Key)
            .ToList();

        // If not enough non-hot candidates, take from all entries
        if (candidates.Count < count)
        {
            var remaining = count - candidates.Count;
            var hotEvictions = _entries
                .Where(kvp => !candidates.Contains(kvp.Key))
                .OrderBy(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
                .Take(remaining)
                .Select(kvp => kvp.Key);
            candidates.AddRange(hotEvictions);
        }

        foreach (var key in candidates)
            _entries.TryRemove(key, out _);
    }

    /// <summary>
    ///     Halve all access counts to prevent LFU starvation.
    ///     Old entries that stop being accessed will gradually lose their accumulated counts.
    /// </summary>
    private void AgeAccessCounts()
    {
        foreach (var kvp in _entries)
        {
            // Atomic read-then-halve (approximate — good enough for LFU heuristic)
            var current = Interlocked.Read(ref kvp.Value.AccessCount);
            Interlocked.Exchange(ref kvp.Value.AccessCount, current / 2);
        }
    }
}

/// <summary>
///     Per-signature aggregate data maintained by the write-through cache.
///     All field mutations must be guarded by <see cref="SyncRoot"/>.
/// </summary>
public sealed class SignatureAggregate
{
    public int HitCount;
    public string? BotName;
    public string? BotType;
    public string? RiskBand;
    public double BotProbability;
    public double Confidence;
    public string? Action;
    public string? CountryCode;
    public double ProcessingTimeMs;
    public List<string>? TopReasons;
    public DateTime LastSeen;
    public string? Narrative;
    public string? Description;
    public bool IsBot;
    public double? ThreatScore;
    public string? ThreatBand;

    /// <summary>LFU access counter — incremented on read, periodically aged.</summary>
    public long AccessCount;

    /// <summary>Ring buffer of recent bot probability scores for sparkline.</summary>
    public readonly LinkedList<double> ScoreHistory = new();

    /// <summary>Sync root for all field mutations.</summary>
    public readonly object SyncRoot = new();
}
