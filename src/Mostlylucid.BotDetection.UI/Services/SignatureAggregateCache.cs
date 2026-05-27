using System.Collections.Concurrent;
using Mostlylucid.BotDetection.UI.Configuration;
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
///     to amortize cost - eviction only triggers when 10% over capacity.
///     </para>
/// </summary>
public sealed class SignatureAggregateCache
{
    private readonly ConcurrentDictionary<string, SignatureAggregate> _entries = new();
    private readonly object _sortLock = new();
    private readonly StyloBotDashboardOptions _options;
    private IReadOnlyList<DashboardTopBotEntry>? _sortedCache;
    private volatile bool _sortDirty = true;
    private long _updateCounter;

    public SignatureAggregateCache(StyloBotDashboardOptions options)
    {
        _options = options;
    }

    /// <summary>Maximum entries before LFU eviction kicks in.</summary>
    public int MaxEntries { get; init; } = 200;

    /// <summary>Number of score history points to keep per signature (for sparklines).</summary>
    public int ScoreHistorySize { get; init; } = 20;

    /// <summary>Age access counts every N updates to prevent LFU starvation.</summary>
    private const int AccessCountAgingInterval = 500;

    /// <summary>HitCount threshold below which an entry is a candidate for LFU eviction.</summary>
    private const int EvictionHotThreshold = 10;

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
        string? sortDir = null,
        string? filterCountry = null,
        string? filter = null)
    {
        // For "bots" (default) use the pre-built sorted list of bot-only entries.
        // For "all" or "humans", we need all entries from the dictionary.
        IEnumerable<DashboardTopBotEntry> query;
        if (string.IsNullOrEmpty(filter) || filter == "bots")
        {
            query = GetOrRebuildSortedList();
        }
        else
        {
            var all = _entries.Select(kvp => ToEntry(kvp.Key, kvp.Value));
            query = filter == "humans"
                ? all.Where(b => !b.IsKnownBot)
                : all; // "all"
        }

        if (!string.IsNullOrEmpty(filterCountry))
            query = query.Where(b =>
                string.Equals(b.CountryCode, filterCountry, StringComparison.OrdinalIgnoreCase));

        var asc = string.Equals(sortDir, "asc", StringComparison.OrdinalIgnoreCase);

        query = (sortBy?.ToLowerInvariant()) switch
        {
            "name" => asc
                ? query.OrderBy(b => b.BotName ?? b.PrimarySignature)
                : query.OrderByDescending(b => b.BotName ?? b.PrimarySignature),
            "lastseen" => asc
                ? query.OrderBy(b => b.LastSeen)
                : query.OrderByDescending(b => b.LastSeen),
            "country" => asc
                ? query.OrderBy(b => b.CountryCode ?? "ZZ")
                : query.OrderByDescending(b => b.CountryCode ?? "ZZ"),
            "probability" => asc
                ? query.OrderBy(b => b.BotProbability)
                : query.OrderByDescending(b => b.BotProbability),
            "threat" => asc
                ? query.OrderBy(b => b.ThreatScore ?? 0)
                : query.OrderByDescending(b => b.ThreatScore ?? 0),
            "hits" => asc
                ? query.OrderBy(b => b.HitCount)
                : query.OrderByDescending(b => b.HitCount),
            // Default: composite sort - threat score * hits for a blended ranking
            _ => query.OrderByDescending(b => (b.ThreatScore ?? 0) * 0.4 + Math.Log10(Math.Max(b.HitCount, 1)) * 0.6)
        };

        return query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
    }

    /// <summary>
    ///     Pre-warm the per-signature hit ring buffers from recent stored detections.
    ///     Walked oldest-first; signatures not in the cache (outside the warm-up top-N)
    ///     are silently skipped. Without this, the sparkline column reads as flat for
    ///     the first 60 minutes after every restart -- the user's "no in-memory stores
    ///     that don't pre-warm" rule.
    /// </summary>
    public void SeedHitTrendsFromDetections(IEnumerable<DashboardDetectionEvent> recentDetections)
    {
        foreach (var d in recentDetections)
        {
            if (string.IsNullOrEmpty(d.PrimarySignature)) continue;
            if (!_entries.TryGetValue(d.PrimarySignature, out var agg)) continue;
            lock (agg.SyncRoot)
            {
                agg.RecordHit(d.Timestamp.ToUniversalTime());
            }
        }
    }

    /// <summary>
    ///     Snapshot of how many cached signatures fall into each filter bucket. Single
    ///     pass over the entries dictionary -- much cheaper than calling
    ///     <see cref="GetTopBots"/> three times with different filters just to count.
    /// </summary>
    public TopBotsCounts GetCounts()
    {
        int bots = 0, humans = 0;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.IsBot) bots++; else humans++;
        }
        return new TopBotsCounts(All: bots + humans, Bots: bots, Humans: humans);
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
                // Carry the UA family the event store derived for us at warmup
                // time. Without this, every seeded row starts with UaFamily=null
                // and the Live Activity rows read "GB User" instead of
                // "GB Chrome User" until a new live-traffic detection refreshes
                // the aggregate.
                UaFamily = bot.UaFamily,
            });
        }

        _sortDirty = true;
    }

    /// <summary>
    ///     Warm the cache for a signature by reading recent detection rows and folding
    ///     them into an aggregate. The single read source for every dashboard surface
    ///     is this cache; on a miss the caller hands the persisted detections to this
    ///     method, the cache holds the warmed aggregate, and the caller re-reads via
    ///     <see cref="TryGet"/>. Risk band and threat band are resolved by majority
    ///     vote across the supplied detections so the warmed value cannot disagree
    ///     with the rolling cache value the live-traffic write path produces.
    /// </summary>
    public SignatureAggregate WarmFromDetections(
        string signature,
        IReadOnlyList<DashboardDetectionEvent> detections)
    {
        if (string.IsNullOrEmpty(signature) || detections.Count == 0)
            return null!;

        // Majority-vote on risk_band / threat_band so a single anomalous detection
        // can't flip the headline value the way detections[0] could. Ties resolve
        // to the highest-severity band so the operator never sees an under-call.
        var riskBand = MajorityBand(detections, d => d.RiskBand, RiskSeverity);
        var threatBand = MajorityBand(detections, d => d.ThreatBand, ThreatSeverity);

        // Latest semantics for everything else -- the values the live-traffic Update
        // would write for the most recent detection. detections[0] is the freshest
        // row by the GetDetectionsAsync timestamp DESC ordering.
        var latest = detections[0];

        var agg = new SignatureAggregate
        {
            HitCount = detections.Count,
            BotName = detections.Select(d => d.BotName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? latest.BotName,
            BotType = detections.Select(d => d.BotType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? latest.BotType,
            RiskBand = riskBand,
            BotProbability = latest.BotProbability,
            Confidence = latest.Confidence,
            Action = latest.Action,
            CountryCode = latest.CountryCode,
            ProcessingTimeMs = latest.ProcessingTimeMs,
            TopReasons = latest.TopReasons,
            FirstSeen = detections[^1].Timestamp,
            LastSeen = latest.Timestamp,
            Narrative = latest.Narrative,
            Description = latest.Description,
            IsBot = latest.IsBot,
            ThreatScore = latest.ThreatScore,
            ThreatBand = threatBand,
            RiskJustification = latest.RiskJustification,
            // ANY detection in the window that confirmed verification latches the
            // aggregate as verified -- same sticky-true semantics as the live
            // Update path. A quorum-exit detection that skipped VerifiedBotContributor
            // does not erase a verified state observed from an earlier detection.
            IsVerifiedBot = detections.Any(d => d.IsVerifiedBot),
            // Warmup events have empty ImportantSignals (the SignalR enrichment
            // doesn't survive the persistence round-trip), so we derive UaFamily
            // from the stored UA string the same way VisitorListCache does --
            // first non-empty signal wins, then UA-string parse as the fallback.
            UaFamily = detections
                .Select(ExtractUaFamilySignal)
                .FirstOrDefault(f => !string.IsNullOrEmpty(f))
                ?? VisitorListCache.DeriveUaFamily(latest.UserAgentRaw ?? latest.UserAgent)
        };

        // Score history walks oldest-to-newest so the sparkline reads left-to-right.
        foreach (var d in detections.Reverse())
        {
            agg.ScoreHistory.AddLast(d.BotProbability);
            while (agg.ScoreHistory.Count > ScoreHistorySize)
                agg.ScoreHistory.RemoveFirst();
        }

        _entries[signature] = agg;
        _sortDirty = true;
        return agg;
    }

    private static string? MajorityBand<T>(
        IReadOnlyList<T> rows, Func<T, string?> selector, Func<string, int> severity)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var b = selector(r);
            if (string.IsNullOrEmpty(b)) continue;
            counts[b] = counts.GetValueOrDefault(b) + 1;
        }
        if (counts.Count == 0) return null;
        var maxCount = counts.Values.Max();
        return counts
            .Where(kv => kv.Value == maxCount)
            .OrderByDescending(kv => severity(kv.Key))
            .First().Key;
    }

    private static int RiskSeverity(string band) => band switch
    {
        "VeryHigh" => 5, "High" => 4, "Elevated" => 3, "Medium" => 3,
        "Low" => 2, "VeryLow" => 1, _ => 0
    };

    private static int ThreatSeverity(string band) => band switch
    {
        "Critical" => 5, "High" => 4, "Elevated" => 3, "Low" => 2, _ => 0
    };

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
            FirstSeen = detection.Timestamp,
            LastSeen = detection.Timestamp,
            Narrative = detection.Narrative,
            Description = detection.Description,
            IsBot = detection.IsBot,
            ThreatScore = detection.ThreatScore,
            ThreatBand = detection.ThreatBand,
            IsVerifiedBot = detection.IsVerifiedBot,
            UaFamily = ExtractUaFamilySignal(detection),
        };

        // No lock needed - object is not yet visible to other threads
        agg.ScoreHistory.AddLast(detection.BotProbability);
        agg.RecordHit(detection.Timestamp.ToUniversalTime());

        return agg;
    }

    /// <summary>
    ///     Mirror of <c>VisitorListCache.ExtractSignal("ua.family")</c>. Reads
    ///     the UA family the detection pipeline emits on every request from
    ///     <see cref="DashboardDetectionEvent.ImportantSignals"/>. We do NOT
    ///     also derive from <c>UserAgentRaw</c> here -- the live path always
    ///     populates the signal; warmed-from-store events for SignatureAggregate
    ///     come through a different code path that already supplies the
    ///     aggregate fields directly.
    /// </summary>
    private static string? ExtractUaFamilySignal(DashboardDetectionEvent detection)
    {
        if (detection.ImportantSignals is null) return null;
        if (detection.ImportantSignals.TryGetValue("ua.family", out var v) && v is not null)
        {
            var s = v.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        return null;
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
            existing.IsBot = detection.IsBot;
            // Name + type are owned by the canonical naming pipeline (UpdateSignatureBotNameAsync
            // calling ApplyBotName, write-through to the dashboard_signatures table). Per-detection
            // updates must NEVER overwrite a name that's already been set -- otherwise an early
            // heuristic guess like "British Suspicious Client" clobbers a later LLM-resolved name,
            // or vice versa, and the cache drifts permanently away from the persistent store.
            // The only mutation allowed here is the first-time seed when the cache has no name yet.
            if (string.IsNullOrEmpty(existing.BotName) && !string.IsNullOrEmpty(detection.BotName))
            {
                existing.BotName = detection.BotName;
                existing.BotType = detection.BotType;
            }
            else if (string.IsNullOrEmpty(existing.BotType) && !string.IsNullOrEmpty(detection.BotType))
            {
                existing.BotType = detection.BotType;
            }
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
            existing.ThreatScore = detection.ThreatScore ?? existing.ThreatScore;
            existing.ThreatBand = detection.ThreatBand ?? existing.ThreatBand;
            // Latch verified-bot true forever. A confirmed Googlebot signature does
            // not "un-verify" on a subsequent request that happened to skip the
            // verifier (e.g., quorum-exit before VerifiedBotContributor ran), so
            // OR-in rather than overwrite.
            existing.IsVerifiedBot |= detection.IsVerifiedBot;
            // UaFamily can only IMPROVE: seed from the first non-empty signal we
            // see and never overwrite with null (some detection paths quorum-exit
            // before UA-family resolution and emit no ua.family signal).
            if (string.IsNullOrEmpty(existing.UaFamily))
            {
                var fam = ExtractUaFamilySignal(detection);
                if (!string.IsNullOrEmpty(fam)) existing.UaFamily = fam;
            }
            // RiskJustification is the rendered "why this band" string -- it must track
            // the CURRENT band (which we always overwrite on detection at line 363),
            // not the first one we ever saw. Previously this coalesced with `??`, so
            // a signature whose first detection produced "AI probability 0.92" would
            // keep that justification forever even after every subsequent detection
            // produced a different reason set (or no reason at all). The trace strings
            // and the band itself disagreed in the signature detail UI.
            existing.RiskJustification = detection.RiskJustification;

            existing.ScoreHistory.AddLast(detection.BotProbability);
            while (existing.ScoreHistory.Count > ScoreHistorySize)
                existing.ScoreHistory.RemoveFirst();

            existing.RecordHit(detection.Timestamp.ToUniversalTime());
        }

        return existing;
    }

    private IReadOnlyList<DashboardTopBotEntry> GetOrRebuildSortedList()
    {
        // Fast path - double-checked locking. Benign staleness is acceptable for a dashboard.
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

    private DashboardTopBotEntry ToEntry(string signature, SignatureAggregate agg)
    {
        _options.SignatureLabels.TryGetValue(signature, out var customName);
        lock (agg.SyncRoot)
        {
            return new DashboardTopBotEntry
            {
                PrimarySignature = signature,
                HitCount = agg.HitCount,
                BotName = agg.BotName,
                CustomBotName = customName,
                BotType = agg.BotType,
                RiskBand = agg.RiskBand,
                BotProbability = agg.BotProbability,
                Confidence = agg.Confidence,
                Action = agg.Action,
                CountryCode = agg.CountryCode,
                ProcessingTimeMs = agg.ProcessingTimeMs,
                TopReasons = agg.TopReasons,
                FirstSeen = agg.FirstSeen,
                LastSeen = agg.LastSeen,
                Narrative = agg.Narrative,
                Description = agg.Description,
                IsKnownBot = agg.IsBot,
                ThreatScore = agg.ThreatScore,
                ThreatBand = agg.ThreatBand,
                IsVerifiedBot = agg.IsVerifiedBot,
                UaFamily = agg.UaFamily,
                HitTrend = agg.ReadHitTrend(),
            };
        }
    }

    /// <summary>
    ///     Batch eviction: remove the N entries with the lowest AccessCount,
    ///     skipping hot entries (high HitCount) unless all entries are hot.
    /// </summary>
    private void EvictLfuBatch(int count)
    {
        var candidateSet = new HashSet<string>(
            _entries
                .Where(kvp => kvp.Value.HitCount <= EvictionHotThreshold)
                .OrderBy(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
                .Take(count)
                .Select(kvp => kvp.Key),
            StringComparer.Ordinal);

        // If not enough non-hot candidates, take from all entries (O(1) lookup via HashSet)
        if (candidateSet.Count < count)
        {
            var remaining = count - candidateSet.Count;
            foreach (var key in _entries
                .Where(kvp => !candidateSet.Contains(kvp.Key))
                .OrderBy(kvp => Interlocked.Read(ref kvp.Value.AccessCount))
                .Take(remaining)
                .Select(kvp => kvp.Key))
            {
                candidateSet.Add(key);
            }
        }

        foreach (var key in candidateSet)
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
            // Atomic read-then-halve (approximate - good enough for LFU heuristic)
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
    public DateTime FirstSeen;
    public DateTime LastSeen;
    public string? Narrative;
    public string? Description;
    public bool IsBot;
    public double? ThreatScore;
    public string? ThreatBand;
    public string? RiskJustification;

    /// <summary>
    ///     Latched on the first detection that wrote <c>verifiedbot.confirmed=true</c>.
    ///     Stays true once verified (a Googlebot signature does not "un-verify" between
    ///     requests). Flows to <c>DashboardTopBotEntry.IsVerifiedBot</c> so the row's
    ///     verification badge shows the green tick instead of the amber `?`.
    /// </summary>
    public bool IsVerifiedBot;

    /// <summary>
    ///     UA family (Chrome / Firefox / curl / ...) extracted from the
    ///     detection event's <c>ImportantSignals["ua.family"]</c> signal at
    ///     write time. Drives the composite "{Country} {UaFamily} {Role}"
    ///     label form in <see cref="SignatureDisplayName"/>; without it the
    ///     dashboard rows degrade to "GB User" instead of "GB Chrome User".
    /// </summary>
    public string? UaFamily;

    /// <summary>LFU access counter - incremented on read, periodically aged.</summary>
    public long AccessCount;

    /// <summary>Ring buffer of recent bot probability scores for sparkline.</summary>
    public readonly LinkedList<double> ScoreHistory = new();

    /// <summary>
    ///     Per-minute hit count over the last 60 minutes. Index 0 is the most recent
    ///     minute, index 59 is 59 minutes ago. Walked oldest-to-newest by reading
    ///     index 59 down to 0 (see <see cref="ReadHitTrend"/>).
    /// </summary>
    private readonly int[] _hitsPerMinute = new int[60];

    /// <summary>UTC minute corresponding to <c>_hitsPerMinute[0]</c>; advances on update.</summary>
    private DateTime _hitsBucketMinute = DateTime.MinValue;

    /// <summary>
    ///     Record one hit at the supplied UTC timestamp. Caller must already hold
    ///     <see cref="SyncRoot"/> (CreateNew runs before the aggregate is published,
    ///     Update wraps in lock).
    /// </summary>
    internal void RecordHit(DateTime utcTimestamp)
    {
        var thisMin = new DateTime(
            utcTimestamp.Year, utcTimestamp.Month, utcTimestamp.Day,
            utcTimestamp.Hour, utcTimestamp.Minute, 0, DateTimeKind.Utc);

        if (_hitsBucketMinute == DateTime.MinValue)
        {
            _hitsBucketMinute = thisMin;
            _hitsPerMinute[0] = 1;
            return;
        }

        var minutesAdvanced = (int)(thisMin - _hitsBucketMinute).TotalMinutes;
        if (minutesAdvanced < 0)
        {
            // Clock skew or out-of-order detection. Drop into the current bucket
            // rather than rewinding history.
            _hitsPerMinute[0]++;
            return;
        }

        if (minutesAdvanced >= 60)
        {
            Array.Clear(_hitsPerMinute, 0, 60);
        }
        else if (minutesAdvanced > 0)
        {
            // Shift the array right so the "current minute" slot is freed at index 0.
            // The values previously at index N now sit at index N+minutesAdvanced.
            Array.Copy(_hitsPerMinute, 0, _hitsPerMinute, minutesAdvanced, 60 - minutesAdvanced);
            Array.Clear(_hitsPerMinute, 0, minutesAdvanced);
        }

        _hitsBucketMinute = thisMin;
        _hitsPerMinute[0]++;
    }

    /// <summary>
    ///     Return the hit trend oldest-first (caller already holds <see cref="SyncRoot"/>).
    ///     Result[0] is 59 minutes ago, Result[59] is the most recent minute. Returns
    ///     <see cref="Array.Empty{T}"/> when the buffer has never recorded a hit.
    /// </summary>
    internal int[] ReadHitTrend()
    {
        if (_hitsBucketMinute == DateTime.MinValue) return Array.Empty<int>();
        var trend = new int[60];
        for (int i = 0; i < 60; i++) trend[i] = _hitsPerMinute[59 - i];
        return trend;
    }

    /// <summary>Sync root for all field mutations.</summary>
    public readonly object SyncRoot = new();
}