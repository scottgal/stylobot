using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Grouping;
using Mostlylucid.BotDetection.Identity;
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
///     MaxEntries is capped at 500 by default -- matches the visitor-list depth the
///     pre-collapse VisitorListCache held, so dashboard surfaces that read from this
///     cache don't silently shrink their universe of actors. Eviction scans are O(n)
///     but batched to amortize cost - eviction only triggers when 10% over capacity.
///     </para>
/// </summary>
public sealed class SignatureAggregateCache
{
    private readonly ConcurrentDictionary<string, SignatureAggregate> _entries = new();

    /// <summary>
    ///     Resolved display names indexed by primary signature. Populated EXCLUSIVELY by
    ///     <see cref="TryApplyStoreResolvedName"/> -- the only entry point that callers
    ///     wire to <c>IFingerprintStore.GetDisplayNamesBySignaturesAsync</c>, the
    ///     contract-gated read of the canonical <c>Fingerprint.DisplayName</c>. The
    ///     per-detection write path does NOT touch this dict, so a banned-shape name
    ///     ("Win Chrome 149", "Akkoma ...") on a transient <c>DashboardDetectionEvent.BotName</c>
    ///     can never bleed into the dashboard rows. <see cref="ToEntry"/>,
    ///     <see cref="ToProjectedVisitor"/>, and the warmed cold-load paths read off this
    ///     dict; lookups not yet populated leave the row's name null until a refresh
    ///     pulls it from the store.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _resolvedNames =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Resolved score/verdict scalars indexed by primary signature. Populated
    ///     EXCLUSIVELY by <see cref="ApplyResolvedVerdicts"/> -- the read-through of
    ///     the canonical fingerprint LFU (<c>IFingerprintStore.GetResolvedVerdictsBySignaturesAsync</c>),
    ///     the single source for probability / risk band / bot type / confidence /
    ///     threat / is-bot / verified. The per-detection write path does NOT touch
    ///     this dict, so a stale 0.9 / "Chrome 122" / "Unknown" on a transient
    ///     detection event can never bleed into the dashboard rows -- EXACTLY the
    ///     name read-through pattern, applied to the verdict. <see cref="ToEntry"/>,
    ///     <see cref="Project"/>, and the sorted top-bots list read off this dict;
    ///     signatures not yet populated read as 0/null defaults until a refresh
    ///     pulls the verdict from the store.
    /// </summary>
    private readonly ConcurrentDictionary<string, ResolvedVerdict> _resolvedVerdicts =
        new(StringComparer.Ordinal);

    private readonly object _sortLock = new();
    private readonly StyloBotDashboardOptions _options;
    private readonly IBehaviouralGrouper? _grouper;
    private readonly IDashboardEventStore? _eventStore;
    private readonly IFingerprintStore? _fingerprintStore;
    private readonly ILogger<SignatureAggregateCache>? _logger;
    private IReadOnlyList<DashboardTopBotEntry>? _sortedCache;
    private volatile bool _sortDirty = true;
    private long _updateCounter;

    public SignatureAggregateCache(StyloBotDashboardOptions options)
        : this(options, null, null, null, null) { }

    public SignatureAggregateCache(StyloBotDashboardOptions options, IBehaviouralGrouper? grouper)
        : this(options, grouper, null, null, null) { }

    /// <summary>
    ///     DI-friendly ctor. <see cref="IBehaviouralGrouper"/> drives the
    ///     row-collapse logic in <see cref="GetFiltered"/> (verified bots that share
    ///     an identity name fold into one card). <see cref="IDashboardEventStore"/>
    ///     and <see cref="IFingerprintStore"/> are the cold-tier the cache writes
    ///     through to when a bot name is applied -- the cache owns durability so
    ///     callers never need to dual-write (the parasitic write paths were the
    ///     source of "two names at the same instant" regressions). All are
    ///     optional so test fixtures can construct without the stores.
    /// </summary>
    public SignatureAggregateCache(
        StyloBotDashboardOptions options,
        IBehaviouralGrouper? grouper,
        IDashboardEventStore? eventStore,
        IFingerprintStore? fingerprintStore,
        ILogger<SignatureAggregateCache>? logger)
    {
        _options = options;
        _grouper = grouper;
        _eventStore = eventStore;
        _fingerprintStore = fingerprintStore;
        _logger = logger;
    }

    /// <summary>Maximum entries before LFU eviction kicks in. 500 matches the
    /// pre-collapse VisitorListCache depth so dashboard surfaces don't shrink.</summary>
    public int MaxEntries { get; init; } = 500;

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
    ///     Push a batch of store-resolved display names into the projection-time
    ///     name dict. Callers fetch the names via
    ///     <c>IFingerprintStore.GetDisplayNamesBySignaturesAsync</c> -- the
    ///     contract-gated read of the canonical <c>Fingerprint.DisplayName</c> --
    ///     and hand them to this method. This is the ONLY writer to
    ///     <see cref="_resolvedNames"/>; the per-detection write path no longer
    ///     populates names anywhere, so banned-shape detection-event values cannot
    ///     enter the dashboard surface. Null/empty values clear an existing entry.
    /// </summary>
    public void ApplyResolvedNames(IReadOnlyDictionary<string, string?> resolved)
    {
        foreach (var kv in resolved)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (string.IsNullOrEmpty(kv.Value))
                _resolvedNames.TryRemove(kv.Key, out _);
            else
                _resolvedNames[kv.Key] = kv.Value;
        }
        _sortDirty = true;
    }

    /// <summary>
    ///     Read a previously-applied resolved name. Returns null when the cache
    ///     hasn't been seeded for this signature yet -- callers should treat null
    ///     as "no name yet" and either skip the row or fall through to the next
    ///     available label (entity id, UA family). Never fall back to a transient
    ///     detection-event value; that defeats the parasitic-write fix.
    /// </summary>
    public string? GetResolvedName(string signature)
        => _resolvedNames.TryGetValue(signature, out var n) ? n : null;

    /// <summary>
    ///     Push a batch of store-resolved verdict scalars into the projection-time
    ///     verdict dict. Callers fetch the verdicts via
    ///     <c>IFingerprintStore.GetResolvedVerdictsBySignaturesAsync</c> -- the
    ///     read-through of the canonical fingerprint LFU -- and hand them to this
    ///     method. This is the ONLY writer to <see cref="_resolvedVerdicts"/>; the
    ///     per-detection write path no longer stores probability / risk band / bot
    ///     type / verdict anywhere on the aggregate, so a stale detection-event value
    ///     cannot enter the dashboard surface. Null values clear an existing entry.
    /// </summary>
    public void ApplyResolvedVerdicts(IReadOnlyDictionary<string, ResolvedVerdict> resolved)
    {
        foreach (var kv in resolved)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (kv.Value is null)
                _resolvedVerdicts.TryRemove(kv.Key, out _);
            else
                _resolvedVerdicts[kv.Key] = kv.Value;
        }
        _sortDirty = true;
    }

    /// <summary>
    ///     Read a previously-applied resolved verdict. Returns null when the cache
    ///     hasn't been seeded for this signature yet -- callers should treat null as
    ///     "no verdict yet" and fall through to 0/null defaults, the same way
    ///     <see cref="GetResolvedName"/> returns null until a store read populates it.
    ///     Never fall back to a transient detection-event value; that defeats the
    ///     single-source read-through.
    /// </summary>
    public ResolvedVerdict? GetResolvedVerdict(string signature)
        => _resolvedVerdicts.TryGetValue(signature, out var v) ? v : null;

    /// <summary>
    ///     Apply an LLM-generated description (per-signature transient narrative) to
    ///     the in-memory aggregate. The bot NAME is owned by the fingerprint LFU dict
    ///     (Fingerprint.DisplayName) -- written by the matcher's EmitDisplayNameSignal
    ///     and by the LLM rename callback's direct <c>IFingerprintStore</c> call. The
    ///     description is per-signature transient, so it stays on the aggregate.
    /// </summary>
    public void ApplyDescription(string signature, string? description)
    {
        if (description is null) return;
        if (!_entries.TryGetValue(signature, out var agg)) return;
        lock (agg.SyncRoot)
        {
            agg.Description = description;
        }
        _sortDirty = true;
    }

    /// <summary>
    ///     Apply a scoring narrative to the in-memory aggregate. Narratives are
    ///     transient -- they describe the most recent scoring pass, not durable
    ///     identity -- so this is in-memory only.
    /// </summary>
    public void ApplyNarrative(string signature, string narrative)
    {
        if (!_entries.TryGetValue(signature, out var agg)) return;
        lock (agg.SyncRoot)
        {
            agg.Narrative = narrative;
        }
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
    ///     Surface the live per-minute hit trend for one signature so callers that
    ///     fetch DashboardTopBotEntry through the event store (which can't carry a
    ///     ring buffer) can overlay the cache's authoritative trend before rendering.
    ///     Returns <c>false</c> when the signature isn't in the cache or the ring
    ///     buffer has never been seeded.
    /// </summary>
    public bool TryGetHitTrend(string signature, out int[] trend)
    {
        if (!string.IsNullOrEmpty(signature) && _entries.TryGetValue(signature, out var agg))
        {
            lock (agg.SyncRoot)
            {
                var t = agg.ReadHitTrend();
                if (t.Length > 0)
                {
                    trend = t;
                    return true;
                }
            }
        }
        trend = Array.Empty<int>();
        return false;
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
        // Internal (BotType.Internal -> loopback / RFC1918 / docker bridge / self-
        // traffic) is hidden from the All / Bots / Humans chips by default and
        // surfaced only by the dedicated Internal chip. See SbWidgetBatchMiddleware
        // .BuildTopBotsModel for the rationale.
        int bots = 0, humans = 0, internalCount = 0;
        foreach (var kvp in _entries)
        {
            // BotType + is-bot are read through the fingerprint LFU (single source);
            // a signature with no resolved verdict yet counts as a (non-internal) human
            // until its verdict is pulled, the same latency the top-bots list has.
            var verdict = GetResolvedVerdict(kvp.Key);
            if (string.Equals(verdict?.BotType, "Internal", StringComparison.OrdinalIgnoreCase))
                internalCount++;
            else if (verdict?.IsBot == true)
                bots++;
            else
                humans++;
        }
        return new TopBotsCounts(All: bots + humans, Bots: bots, Humans: humans, Internal: internalCount);
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
    ///     Try to get aggregate data for a specific signature from the hot tier.
    ///     Does NOT fall through to the durable store -- callers that want the
    ///     transparent layered read use <see cref="GetOrLoadAsync"/> instead.
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
    ///     Transparent layered read: hot tier first (the LFU dict), cold tier
    ///     (<see cref="IDashboardEventStore"/>) on miss with auto-populate so the
    ///     next caller hits hot. This is the EF-L2 shape the user asked for --
    ///     no read surface should have to know about a cold-tier fallback path;
    ///     they ask the cache, and the cache is always at-least-as-fresh-as-DB
    ///     because every writer goes through it.
    ///     <para>
    ///     A miss with no event store registered (test fixtures, OSS hosts that
    ///     don't wire one) returns null without throwing -- the caller still sees
    ///     "unknown signature" rather than a 500.
    ///     </para>
    ///     <para>
    ///     Concurrent misses for the same signature race the cold-tier read; both
    ///     populate via <see cref="WarmFromDetections"/> which is idempotent
    ///     (last-write-wins on identical data). No per-signature locking -- the
    ///     extra DB call on the loser is cheaper than the lock-table bookkeeping
    ///     for the steady state where misses are rare.
    ///     </para>
    /// </summary>
    public async Task<SignatureAggregate?> GetOrLoadAsync(
        string signature, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        if (_entries.TryGetValue(signature, out var hot))
        {
            Interlocked.Increment(ref hot.AccessCount);
            return hot;
        }
        if (_eventStore is null) return null;

        IReadOnlyList<DashboardDetectionEvent> detections;
        try
        {
            detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
            {
                SignatureId = signature,
                Limit = ScoreHistorySize * 2
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetOrLoadAsync cold-tier read failed for {Signature}",
                signature[..Math.Min(8, signature.Length)]);
            return null;
        }

        if (detections.Count == 0) return null;

        var agg = WarmFromDetections(signature, (IReadOnlyList<DashboardDetectionEvent>)detections);
        if (agg is not null) Interlocked.Increment(ref agg.AccessCount);

        // Cold-tier inserts bypass UpdateFromDetection's eviction trigger. On a remote-mode
        // dashboard host where DetectionBroadcastMiddleware never runs, an operator browsing
        // many cold signatures would grow the dict unboundedly without this check.
        var overage = _entries.Count - MaxEntries;
        if (overage > MaxEntries / 10)
            EvictLfuBatch(overage);

        return agg;
    }

    /// <summary>
    ///     Seed cache from event store data on startup.
    /// </summary>
    public void SeedFromTopBots(IEnumerable<DashboardTopBotEntry> topBots)
    {
        foreach (var bot in topBots)
        {
            // The seed event store's bot_name column was historically written
            // by the (parasitic) detection.BotName path, so we do NOT copy it
            // into _resolvedNames here. Names are seeded ONLY by
            // ApplyResolvedNames callers feeding from
            // IFingerprintStore.GetDisplayNamesBySignaturesAsync; the cache stays
            // nameless until a contract-gated fetch arrives.

            var agg = new SignatureAggregate
            {
                HitCount = bot.HitCount,
                CountryCode = bot.CountryCode,
                ProcessingTimeMs = bot.ProcessingTimeMs,
                TopReasons = bot.TopReasons,
                LastSeen = bot.LastSeen,
                Narrative = bot.Narrative,
                Description = bot.Description,
                // Carry the UA family the event store derived for us at warmup
                // time. Without this, every seeded row starts with UaFamily=null
                // and the Live Activity rows read "GB User" instead of
                // "GB Chrome User" until a new live-traffic detection refreshes
                // the aggregate.
                UaFamily = bot.UaFamily,
                UserAgent = bot.UserAgent,
                EntityId = bot.EntityId,
            };

            // Score/verdict scalars are NOT stored on the aggregate -- they are read
            // THROUGH the fingerprint LFU at projection time (single source). Seed the
            // verdict dict from the store's top-bots envelope so a warmed row renders
            // its verdict immediately, superseded by the store read-through as soon as
            // ApplyResolvedVerdicts runs. Empty botType/"Unknown" projects to null so
            // the view falls through rather than showing a placeholder.
            if (!string.IsNullOrEmpty(bot.PrimarySignature))
            {
                var seedBotType = string.IsNullOrEmpty(bot.BotType)
                    || bot.BotType.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    ? null : bot.BotType;
                _resolvedVerdicts[bot.PrimarySignature] = new ResolvedVerdict(
                    BotProbability: bot.BotProbability,
                    RiskBand: bot.RiskBand,
                    BotType: seedBotType,
                    Confidence: bot.Confidence,
                    ThreatScore: bot.ThreatScore,
                    ThreatBand: bot.ThreatBand,
                    IsBot: bot.IsKnownBot,
                    IsVerifiedBot: bot.IsVerifiedBot);
            }

            // Seed the per-minute ring buffer from the source's HitTrend (if any).
            // Remote-mode dashboard hosts get HitTrend already filled in by the
            // gateway's REST envelope; without this, the website rendered the
            // sparkline column as a flat baseline for every row because the local
            // ring buffer was never populated and the live-traffic write path
            // (DetectionBroadcastMiddleware) doesn't run on the website host.
            if (bot.HitTrend is { Length: > 0 } trend)
                agg.SeedHitTrend(trend);

            _entries.TryAdd(bot.PrimarySignature, agg);
        }

        _sortDirty = true;
    }

    /// <summary>
    ///     Warm the cache for a signature by reading recent detection rows and folding
    ///     them into an aggregate. The single read source for every dashboard surface
    ///     is this cache; on a miss the caller hands the persisted detections to this
    ///     method, the cache holds the warmed aggregate, and the caller re-reads via
    ///     <see cref="TryGet"/>. Risk band and threat band are DERIVED from the same
    ///     facts the warmed verdict carries, never voted across the window -- see the
    ///     derivation block below for why.
    /// </summary>
    public SignatureAggregate WarmFromDetections(
        string signature,
        IReadOnlyList<DashboardDetectionEvent> detections)
    {
        if (string.IsNullOrEmpty(signature) || detections.Count == 0)
            return null!;

        // Latest semantics for everything else -- the values the live-traffic Update
        // would write for the most recent detection. detections[0] is the freshest
        // row by the GetDetectionsAsync timestamp DESC ordering.
        var latest = detections[0];

        // Min/max processing time across the warmed window. Live Update tracks these
        // as rolling stats; warmup must seed them or CollapseGroupable's "min across
        // members" math degenerates to double.MaxValue for any group whose members
        // have never seen a live detection since the last cold-load.
        double minProc = 0, maxProc = 0;
        foreach (var d in detections)
        {
            if (d.ProcessingTimeMs > maxProc) maxProc = d.ProcessingTimeMs;
            if (d.ProcessingTimeMs > 0 && (minProc == 0 || d.ProcessingTimeMs < minProc))
                minProc = d.ProcessingTimeMs;
        }

        var stickyBotType = detections.Select(d => d.BotType).FirstOrDefault(t => !string.IsNullOrEmpty(t)) ?? latest.BotType;

        // Sticky-max BotProbability now keys off BotType, not BotName -- per the
        // single-source spec the per-detection BotName is gone. A real catalogue
        // identity always carries a non-Unknown BotType (Googlebot -> SearchEngine,
        // GPTBot -> AICrawler, etc.), so this is the same operator-facing signal
        // without leaking the parasitic name path back in. Keeps the "once a
        // confirmed bot row scores at 1.00 a later 0.20 cannot drag it down"
        // rule intact.
        var hasNamedIdentity = !string.IsNullOrEmpty(stickyBotType)
            && !stickyBotType.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        var stickyBotProbability = hasNamedIdentity
            ? detections.Max(d => d.BotProbability)
            : latest.BotProbability;

        // Bands are DERIVED from the facts seeded alongside them -- never majority-voted
        // across the window.
        //
        // The vote was the operator P0 (2026-08-08): risk_band was the MODE of the window
        // while BotProbability is the sticky MAX of the same window. Mode and max are
        // unrelated statistics, so the headline band and the headline number were free to
        // disagree -- an asset-heavy crawl (mostly low-signal hits, one identifying hit at
        // 0.98) warmed to band=VeryLow + probability=0.98, and _RiskBadge.cshtml renders
        // both in ONE sentence: "Very Low Risk Profile: 98% bot probability".
        //
        // Deriving risk through FingerprintRiskProjection -- the SAME compute site
        // SqliteFingerprintStore.ProjectVerdict uses for the read-through that supersedes
        // this seed -- also means the warmed value agrees with what ApplyResolvedVerdicts
        // later lands, so the row doesn't visibly change underneath the operator.
        // Threat takes LATEST rather than a re-bucket, because the ThreatScore seeded on
        // this verdict is itself latest.ThreatScore -- so band and score come from one
        // row and agree by construction. Re-bucketing would also silently under-call any
        // band the write path had lifted off a pin (see ThreatBandFloor), turning a
        // Critical-with-null-score into None. Same rule as the cached_risk_band removal:
        // a derived value is never stored, and never voted, as if it were a fact.
        var warmVerified = detections.Any(d => d.IsVerifiedBot);
        var riskBand = FingerprintRiskProjection.Compose(
            stickyBotProbability,
            latest.Confidence,
            warmVerified ? "verified" : null,
            stickyBotType,
            signature).RiskBand.ToString();
        var threatBand = latest.ThreatBand;

        var agg = new SignatureAggregate
        {
            HitCount = detections.Count,
            CountryCode = latest.CountryCode,
            ProcessingTimeMs = latest.ProcessingTimeMs,
            MinProcessingTimeMs = minProc,
            MaxProcessingTimeMs = maxProc,
            TopReasons = latest.TopReasons,
            DetectionReasons = ProjectDetectionReasons(latest.DetectorContributions),
            FirstSeen = detections[^1].Timestamp,
            LastSeen = latest.Timestamp,
            Narrative = latest.Narrative,
            Description = latest.Description,
            // Warmup events have empty ImportantSignals (the SignalR enrichment
            // doesn't survive the persistence round-trip), so we derive UaFamily
            // from the stored UA string the same way VisitorListCache does --
            // first non-empty signal wins, then UA-string parse as the fallback.
            UaFamily = detections
                .Select(ExtractUaFamilySignal)
                .FirstOrDefault(f => !string.IsNullOrEmpty(f))
                ?? DeriveUaFamily(latest.UserAgentRaw ?? latest.UserAgent),
            EntityId = detections
                .Select(d => d.EntityId)
                .FirstOrDefault(e => !string.IsNullOrEmpty(e)),
        };

        // Score/verdict scalars are read THROUGH the fingerprint LFU at projection
        // time (single source), not stored on the aggregate. Seed the verdict dict
        // from the warmed detections (majority-band risk/threat + sticky-max
        // probability) so a warmed row renders its verdict immediately; the store
        // read-through supersedes it as soon as ApplyResolvedVerdicts runs. Empty
        // botType/"Unknown" projects to null so the view falls through.
        var warmBotType = string.IsNullOrEmpty(stickyBotType)
            || stickyBotType.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            ? null : stickyBotType;
        _resolvedVerdicts[signature] = new ResolvedVerdict(
            BotProbability: stickyBotProbability,
            RiskBand: riskBand,
            BotType: warmBotType,
            Confidence: latest.Confidence,
            ThreatScore: latest.ThreatScore,
            ThreatBand: threatBand,
            IsBot: latest.IsBot,
            // ANY detection in the window that confirmed verification latches verified
            // -- same sticky-true semantics as the live Update path, and the same latch
            // the risk derivation above feeds to the composer's friendly-pin.
            IsVerifiedBot: warmVerified);

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

    // ─── Visitor-list surface (collapsed from VisitorListCache) ───────────
    // The visitor card / SbVisitorList view used to live behind a second cache
    // that held its own BotName + BotType + RiskBand per signature. Two stores,
    // two write paths, regular divergence ("two names at the same instant").
    // The methods below let the same callers project off this one LFU cache.

    /// <summary>
    ///     Single visitor projection by signature -- equivalent of the old
    ///     <c>VisitorListCache.Get</c>. Returns null when the signature is not
    ///     in the hot tier. Callers wanting cold-tier fallback should use
    ///     <see cref="TryGet" /> + cold-tier seed.
    /// </summary>
    public ProjectedVisitor? GetVisitor(string primarySignature)
    {
        return _entries.TryGetValue(primarySignature, out var agg)
            ? Project(primarySignature, agg)
            : null;
    }

    /// <summary>
    ///     Filter / sort / page the visitor projections. Single source of truth
    ///     for the visitor card list and any other surface that historically
    ///     went through <c>VisitorListCache.GetFiltered</c>. Filtering happens
    ///     against the canonical aggregate state; collapse-by-group folds
    ///     verified-bot identity rows into one card via the behavioural grouper.
    /// </summary>
    public (IReadOnlyList<ProjectedVisitor> Items, int TotalCount, int Page, int PageSize) GetFiltered(
        string? filter, string sortField, string sortDir, int page, int pageSize)
    {
        var snapshot = SnapshotAllAsVisitors();

        IEnumerable<ProjectedVisitor> items = snapshot;

        items = filter switch
        {
            "humans" => items.Where(v => !v.IsBot),
            "bots"   => items.Where(v => v.IsBot),
            "ai"     => items.Where(v => v.IsBot && IsAiBot(v)),
            "search" => items.Where(v => v.IsBot && IsSearchBot(v)),
            "tools"  => items.Where(v => v.IsBot && IsToolBot(v)),
            _        => items
        };

        items = CollapseGroupable(items);

        items = (sortField, sortDir) switch
        {
            ("name", "asc") => items.OrderBy(v => v.BotName ?? v.PrimarySignature),
            ("name", _)     => items.OrderByDescending(v => v.BotName ?? v.PrimarySignature),
            ("hits", "asc") => items.OrderBy(v => v.Hits),
            ("hits", _)     => items.OrderByDescending(v => v.Hits),
            ("risk", "asc") => items.OrderBy(v => RiskOrder(v.RiskBand)),
            ("risk", _)     => items.OrderByDescending(v => RiskOrder(v.RiskBand)),
            (_, "asc")      => items.OrderBy(v => v.LastSeen),
            _               => items.OrderByDescending(v => v.LastSeen)
        };

        var materialized = items.ToList();
        var totalCount = materialized.Count;
        var paged = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (paged, totalCount, page, pageSize);
    }

    /// <summary>Convenience overload preserving the old VisitorListCache shape.</summary>
    public IReadOnlyList<ProjectedVisitor> GetFilteredVisitors(
        string? filter, string sortField, string sortDir, int limit = 50)
        => GetFiltered(filter, sortField, sortDir, page: 1, pageSize: limit).Items;

    /// <summary>
    ///     Filter badge counts. Single pass over the hot tier instead of
    ///     three separate filter calls.
    /// </summary>
    public FilterCounts GetVisitorCounts()
    {
        var all = SnapshotAllAsVisitors();
        // Internal = self-traffic (bot_type == "Internal": loopback / RFC1918 / health probes).
        // Exact marker, matching WidgetRenderHelpers.IsInternal + the middleware + Postgres, so
        // every read site (cache fast-path + event-store projection path) reports the same count
        // to the operator. Replaces the former fuzzy "no-geo + not-bot + low-prob" guess.
        bool IsInternal(ProjectedVisitor v)
            => string.Equals(v.BotType, "Internal", StringComparison.OrdinalIgnoreCase);
        return new FilterCounts
        {
            All      = all.Count,
            Humans   = all.Count(v => !v.IsBot),
            Bots     = all.Count(v =>  v.IsBot),
            Ai       = all.Count(v =>  v.IsBot && IsAiBot(v)),
            Search   = all.Count(v =>  v.IsBot && IsSearchBot(v)),
            Tools    = all.Count(v =>  v.IsBot && IsToolBot(v)),
            Internal = all.Count(IsInternal),
        };
    }

    /// <summary>
    ///     Top-N bot visitors by hit count -- equivalent of the old
    ///     <c>VisitorListCache.GetTopBots(count)</c>. Note this is a DIFFERENT
    ///     surface from <see cref="GetTopBots" /> which returns
    ///     <see cref="DashboardTopBotEntry" /> for the SbTopBots widget.
    /// </summary>
    public IReadOnlyList<ProjectedVisitor> GetTopBotVisitors(int count = 5)
    {
        return SnapshotAllAsVisitors()
            .Where(v => v.IsBot)
            .OrderByDescending(v => v.Hits)
            .Take(count)
            .ToList();
    }

    // ─── Visitor projection internals ─────────────────────────────────────

    private List<ProjectedVisitor> SnapshotAllAsVisitors()
    {
        var result = new List<ProjectedVisitor>(_entries.Count);
        foreach (var kvp in _entries)
            result.Add(Project(kvp.Key, kvp.Value));
        return result;
    }

    /// <summary>
    ///     Project a stable, lock-bounded snapshot of an aggregate as a
    ///     <see cref="ProjectedVisitor" /> for the visitor card / list surfaces.
    ///     Held under SyncRoot so mutable collections (Paths, ring buffers)
    ///     don't tear during the copy. The visible <c>BotName</c> on the
    ///     projection is pulled from <see cref="_resolvedNames"/>, the
    ///     store-gated read of <c>Fingerprint.DisplayName</c>; null when no
    ///     <see cref="ApplyResolvedNames"/> has populated it yet.
    /// </summary>
    private ProjectedVisitor Project(string signature, SignatureAggregate agg)
    {
        // Score/verdict scalars are read THROUGH the fingerprint LFU (single source),
        // exactly like BotName. Null verdict projects to the ProjectedVisitor defaults
        // (IsBot=false, prob=0, RiskBand="Medium", Action="Allow") until the store
        // read-through has populated it.
        var verdict = GetResolvedVerdict(signature);
        lock (agg.SyncRoot)
        {
            return new ProjectedVisitor
            {
                PrimarySignature = signature,
                Hits = agg.HitCount,
                FirstSeen = agg.FirstSeen,
                LastSeen = agg.LastSeen,
                IsBot = verdict?.IsBot ?? false,
                BotProbability = verdict?.BotProbability ?? 0,
                Confidence = verdict?.Confidence ?? 0,
                RiskBand = verdict?.RiskBand ?? "Medium",
                LastPath = agg.LastPath,
                Paths = agg.Paths.ToList(),
                Action = "Allow",
                BotName = GetResolvedName(signature),
                BotType = verdict?.BotType,
                CountryCode = agg.CountryCode,
                UserAgent = agg.UserAgent,
                Narrative = agg.Narrative,
                Description = agg.Description,
                TopReasons = agg.TopReasons?.ToList() ?? new List<string>(),
                DetectionReasons = agg.DetectionReasons?.ToList() ?? new List<DetectionReasonEntry>(),
                ProcessingTimeMs = agg.ProcessingTimeMs,
                MaxProcessingTimeMs = agg.MaxProcessingTimeMs,
                MinProcessingTimeMs = agg.MinProcessingTimeMs,
                ProcessingTimeHistory = new Queue<double>(agg.ProcessingTimeHistory),
                BotProbabilityHistory = new Queue<double>(agg.ScoreHistory),
                ConfidenceHistory = new Queue<double>(agg.ConfidenceHistory),
                LastRequestId = agg.LastRequestId,
                ThreatScore = verdict?.ThreatScore,
                ThreatBand = verdict?.ThreatBand,
                Protocol = agg.Protocol,
                IpSubnetSignature = agg.IpSubnetSignature,
                UaFamily = agg.UaFamily,
                FingerprintId = agg.FingerprintId,
                ClusterId = agg.ClusterId,
                RadarShape = agg.RadarShape,
                GroupKey = agg.GroupKey,
                GroupMemberCount = agg.GroupMemberCount,
            };
        }
    }

    // ─── Collapse-by-group (verified-bot identity folding) ────────────────

    private string ResolveGroupCanonical(ProjectedVisitor v)
    {
        if (_grouper is not null)
            return _grouper.Resolve(BuildGrouperInput(v)).Canonical;

        return Middleware.WidgetRenderHelpers.IsGroupableIdentity(
                   customBotName: null, v.BotName, v.BotType)
            ? "name:" + v.BotName
            : "sig:" + v.PrimarySignature;
    }

    private IEnumerable<ProjectedVisitor> CollapseGroupable(IEnumerable<ProjectedVisitor> source)
    {
        var list = source.ToList();
        // Case-insensitive grouping IS the centroid rule for bot rows. Even after
        // the canonicalisation step that runs at the store-write and broadcast
        // boundaries, stale DB rows from before the canonicaliser landed may
        // still surface "googlebot" / "Googlebot" alongside the canonical
        // entry. Folding them at the group key keeps the live view honest
        // without forcing a DB rewrite.
        foreach (var grp in list.GroupBy(v => ResolveGroupCanonical(v), StringComparer.OrdinalIgnoreCase))
        {
            var members = grp.ToList();
            if (members.Count == 1) { yield return members[0]; continue; }

            var canonical = members.OrderByDescending(v => v.LastSeen).First();
            var resolvedKey = _grouper?.Resolve(BuildGrouperInput(canonical));
            yield return new ProjectedVisitor
            {
                PrimarySignature = canonical.PrimarySignature,
                Hits = members.Sum(v => v.Hits),
                FirstSeen = members.Min(v => v.FirstSeen == default ? DateTime.MaxValue : v.FirstSeen),
                LastSeen = members.Max(v => v.LastSeen),
                IsBot = canonical.IsBot,
                BotProbability = members.Max(v => v.BotProbability),
                Confidence = canonical.Confidence,
                RiskBand = canonical.RiskBand,
                LastPath = canonical.LastPath,
                Paths = canonical.Paths,
                Action = canonical.Action,
                BotName = canonical.BotName,
                BotType = canonical.BotType,
                CountryCode = canonical.CountryCode,
                UserAgent = canonical.UserAgent,
                Narrative = canonical.Narrative,
                Description = canonical.Description,
                TopReasons = canonical.TopReasons,
                DetectionReasons = canonical.DetectionReasons,
                ProcessingTimeMs = canonical.ProcessingTimeMs,
                MaxProcessingTimeMs = members.Max(v => v.MaxProcessingTimeMs),
                // Min across known mins, 0 when every member is unset. The previous
                // sentinel-MaxValue pattern leaked to the UI as 1.79e+308 whenever a
                // group's members had all been warmed from the DB without live traffic
                // (Min/Max default to 0 in that path).
                MinProcessingTimeMs = members.Any(v => v.MinProcessingTimeMs > 0)
                    ? members.Where(v => v.MinProcessingTimeMs > 0).Min(v => v.MinProcessingTimeMs)
                    : 0,
                ProcessingTimeHistory = canonical.ProcessingTimeHistory,
                BotProbabilityHistory = canonical.BotProbabilityHistory,
                ConfidenceHistory = canonical.ConfidenceHistory,
                LastRequestId = canonical.LastRequestId,
                ThreatScore = members.Max(v => v.ThreatScore),
                ThreatBand = canonical.ThreatBand,
                Protocol = canonical.Protocol,
                IpSubnetSignature = canonical.IpSubnetSignature,
                UaFamily = canonical.UaFamily,
                FingerprintId = canonical.FingerprintId,
                ClusterId = canonical.ClusterId,
                GroupKey = resolvedKey,
                GroupMemberCount = members.Count
            };
        }
    }

    private static GroupingInput BuildGrouperInput(ProjectedVisitor v) => new()
    {
        Signature = v.PrimarySignature,
        BotProbability = v.BotProbability,
        RiskBand = v.RiskBand,
        IsBot = v.IsBot,
        BotName = v.BotName,
        BotType = v.BotType,
        IpSubnetSignature = v.IpSubnetSignature,
        UaFamily = v.UaFamily,
        CountryCode = v.CountryCode,
        FingerprintId = v.FingerprintId,
        ClusterId = v.ClusterId
    };

    // ─── Filter predicates + ordering (formerly VisitorListCache statics) ─

    private static bool IsAiBot(ProjectedVisitor v) => v.BotType is "AiBot";
    private static bool IsSearchBot(ProjectedVisitor v) =>
        v.BotType is "SearchEngine" or "VerifiedBot" or "GoodBot";
    private static bool IsToolBot(ProjectedVisitor v) =>
        v.BotType is "Scraper" or "MonitoringBot" or "SocialMediaBot" or "Tool";

    private static int RiskOrder(string? band) => band switch
    {
        "VeryHigh" => 5,
        "High" => 4,
        "Medium" or "Elevated" => 3,
        "Low" => 2,
        "VeryLow" => 1,
        _ => 0
    };

    // REMOVED: MajorityBand / RiskSeverity / ThreatSeverity.
    //
    // These majority-voted risk_band / threat_band across the warmed window while
    // BotProbability was the sticky MAX of that same window -- two unrelated statistics
    // over the same rows, which is how a row came to render "Very Low Risk Profile: 98%
    // bot probability" (operator P0, 2026-08-08). Bands are now derived from the facts
    // they are seeded beside, in WarmFromDetections. Nothing else called them.

    // ─── Internal ────────────────────────────────────────────────────────

    private SignatureAggregate CreateNew(DashboardDetectionEvent detection)
    {
        var path = detection.Path;
        var agg = new SignatureAggregate
        {
            HitCount = 1,
            // Score/verdict scalars (BotType / RiskBand / BotProbability / Confidence /
            // Action / IsBot / ThreatScore / ThreatBand / IsVerifiedBot) are NOT stored
            // here -- they are owned by the fingerprint LFU (single source) and read
            // THROUGH it at projection time via GetResolvedVerdict, exactly like the
            // display name. The per-detection event carries a transient, unvetted copy
            // (a stale 0.9 / "Chrome 122" / "Unknown") that must never bleed into the
            // dashboard rows -- the same parasitic-store fix already applied to the name.
            CountryCode = detection.CountryCode,
            ProcessingTimeMs = detection.ProcessingTimeMs,
            MinProcessingTimeMs = detection.ProcessingTimeMs,
            MaxProcessingTimeMs = detection.ProcessingTimeMs,
            TopReasons = detection.TopReasons,
            DetectionReasons = ProjectDetectionReasons(detection.DetectorContributions),
            FirstSeen = detection.Timestamp,
            LastSeen = detection.Timestamp,
            Narrative = detection.Narrative,
            Description = detection.Description,
            UaFamily = ExtractUaFamilySignal(detection),
            UserAgent = detection.UserAgentRaw ?? detection.UserAgent,
            EntityId = detection.EntityId,
            // ProjectedVisitor-equivalents moved onto the aggregate so the visitor
            // card and the signature detail page read off the same record.
            LastPath = path,
            Paths = string.IsNullOrEmpty(path) ? new List<string>() : new List<string> { path },
            LastRequestId = detection.RequestId,
            Protocol = ExtractProtocolSignal(detection),
            IpSubnetSignature = ExtractSignal(detection, "ip.subnet"),
            FingerprintId = ExtractSignal(detection, "identity.fingerprint_id"),
            ClusterId = ExtractSignal(detection, "cluster.id"),
            RadarShape = detection.RadarShape,
        };

        // No lock needed - object is not yet visible to other threads
        agg.ScoreHistory.AddLast(detection.BotProbability);
        agg.ProcessingTimeHistory.Enqueue(detection.ProcessingTimeMs);
        agg.ConfidenceHistory.Enqueue(detection.Confidence);
        agg.RecordHit(detection.Timestamp.ToUniversalTime());

        return agg;
    }

    /// <summary>
    ///     Read the protocol off the detection's important_signals -- HTTP/1.1
    ///     when nothing more specific is set; HTTP/2 when an h2.* signal is
    ///     present; HTTP/3 when an h3.* signal is present. Mirrors the helper
    ///     that used to live on VisitorListCache.
    /// </summary>
    private static string? ExtractProtocolSignal(DashboardDetectionEvent detection)
    {
        if (detection.ImportantSignals is null) return null;
        if (detection.ImportantSignals.TryGetValue("request.protocol", out var proto))
            return proto?.ToString();
        if (detection.ImportantSignals.ContainsKey("h3.protocol")) return "HTTP/3";
        if (detection.ImportantSignals.ContainsKey("h2.protocol")) return "HTTP/2";
        return null;
    }

    /// <summary>
    ///     Read a single signal value off the detection event by key. Used to
    ///     populate the behavioural-grouper inputs (ip.subnet, identity.fingerprint_id,
    ///     cluster.id) onto the aggregate without expanding the model surface area.
    /// </summary>
    private static string? ExtractSignal(DashboardDetectionEvent detection, string key)
    {
        if (detection.ImportantSignals is null) return null;
        if (detection.ImportantSignals.TryGetValue(key, out var v) && v is not null)
            return v.ToString();
        return null;
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
    ///     Last-ditch UA family derivation when the persisted event store didn't carry an
    ///     ua.family signal (warm-up events strip ImportantSignals).
    /// </summary>
    private static string? DeriveUaFamily(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var family = Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(userAgent).Family;
        return string.IsNullOrEmpty(family) ? null : family;
    }

    /// <summary>
    ///     Project the event's raw per-detector dict down to the lean, capped shape the
    ///     cache stores (see <see cref="SignatureAggregate.DetectionReasons"/>) -- top 6
    ///     by |Contribution|, label-only (no detector Name/ExecutionTimeMs), so this
    ///     doesn't bloat the bounded per-fingerprint LFU cache the way carrying the full
    ///     Dictionary&lt;string, DashboardDetectorContribution&gt; on every entry would.
    ///     Internal (not private) so StyloBotDashboardMiddleware's event-store fallback
    ///     path -- which has its own DashboardDetectionEvent.DetectorContributions, no
    ///     cache involved -- projects the same lean shape without duplicating the logic.
    /// </summary>
    internal static List<DetectionReasonEntry> ProjectDetectionReasons(
        Dictionary<string, DashboardDetectorContribution>? contributions)
    {
        if (contributions is null || contributions.Count == 0) return new List<DetectionReasonEntry>();
        return contributions
            .Select(kv => new DetectionReasonEntry(
                string.IsNullOrEmpty(kv.Value.Reason) ? kv.Key.Replace("Contributor", "", StringComparison.Ordinal) : kv.Value.Reason,
                kv.Value.ConfidenceDelta,
                kv.Value.Contribution))
            .OrderByDescending(e => Math.Abs(e.Contribution))
            .Take(6)
            .ToList();
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
            // Score/verdict scalars (IsBot / BotType / RiskBand / BotProbability /
            // Confidence / Action / ThreatScore / ThreatBand / IsVerifiedBot /
            // RiskJustification) are NO LONGER mutated here. They are owned by the
            // fingerprint LFU (single source) and read THROUGH it at projection time
            // via GetResolvedVerdict, exactly like the display name. The per-detection
            // event's copy is transient and unvetted (a stale 0.9 / "Chrome 122" /
            // "Unknown"); storing a parallel copy here was the dual-store bug where
            // top-bots showed a stale scalar while the fingerprint detail page showed
            // the fresh value. The gateway's write-behind LFU façade keeps the
            // fingerprint's cached_bot_probability / cached_risk_band fresh; the
            // dashboard reads that, not this aggregate.
            existing.CountryCode = detection.CountryCode ?? existing.CountryCode;
            existing.ProcessingTimeMs = detection.ProcessingTimeMs;
            existing.TopReasons = detection.TopReasons ?? existing.TopReasons;
            if (detection.DetectorContributions is { Count: > 0 })
                existing.DetectionReasons = ProjectDetectionReasons(detection.DetectorContributions);
            existing.LastSeen = detection.Timestamp;
            existing.Narrative = detection.Narrative ?? existing.Narrative;
            existing.Description = detection.Description ?? existing.Description;
            // UaFamily can only IMPROVE: seed from the first non-empty signal we
            // see and never overwrite with null (some detection paths quorum-exit
            // before UA-family resolution and emit no ua.family signal).
            if (string.IsNullOrEmpty(existing.UaFamily))
            {
                var fam = ExtractUaFamilySignal(detection);
                if (!string.IsNullOrEmpty(fam)) existing.UaFamily = fam;
            }
            // Raw UA tracks the LATEST detection so an auto-update / UA bump
            // reflects the current version. Never overwrite with null (some
            // detection paths quorum-exit before the UA was serialised onto
            // the broadcast event); a missing UA must not erase a previously
            // populated one or the dashboard's "Chrome 119 / macOS" label
            // would flicker back to "GB User" on every subsequent hit.
            var incomingUa = detection.UserAgentRaw ?? detection.UserAgent;
            if (!string.IsNullOrEmpty(incomingUa)) existing.UserAgent = incomingUa;
            // EntityId latches sticky on the first non-null value. Verdict-cache
            // skip paths sometimes carry no entity id (the gateway didn't resolve
            // one for that request); a missing id MUST NOT erase a previously
            // resolved one or SbTopBots rows would flicker between entity and
            // signature URLs across consecutive detections for the same actor.
            if (string.IsNullOrEmpty(existing.EntityId) && !string.IsNullOrEmpty(detection.EntityId))
                existing.EntityId = detection.EntityId;

            // Sparkline ring: still fed from the per-detection probability so the
            // per-row score history reads left-to-right. This is a transient trend
            // series, not the headline verdict (which is read through the fingerprint
            // LFU via GetResolvedVerdict), so it stays on the aggregate.
            existing.ScoreHistory.AddLast(detection.BotProbability);
            while (existing.ScoreHistory.Count > ScoreHistorySize)
                existing.ScoreHistory.RemoveFirst();

            // ProjectedVisitor-equivalents -- maintained under the same SyncRoot
            // lock so visitor-card / signature-detail reads see consistent state.
            existing.LastPath = detection.Path;
            if (!string.IsNullOrEmpty(detection.Path) && !existing.Paths.Contains(detection.Path))
            {
                existing.Paths.Add(detection.Path);
                if (existing.Paths.Count > 20) existing.Paths.RemoveAt(0);
            }
            existing.LastRequestId = detection.RequestId;
            var proto = ExtractProtocolSignal(detection);
            if (!string.IsNullOrEmpty(proto)) existing.Protocol = proto;
            var subnet = ExtractSignal(detection, "ip.subnet");
            if (!string.IsNullOrEmpty(subnet)) existing.IpSubnetSignature = subnet;
            var fpid = ExtractSignal(detection, "identity.fingerprint_id");
            if (!string.IsNullOrEmpty(fpid)) existing.FingerprintId = fpid;
            var clusterId = ExtractSignal(detection, "cluster.id");
            if (!string.IsNullOrEmpty(clusterId)) existing.ClusterId = clusterId;
            if (detection.RadarShape is { Length: 16 }) existing.RadarShape = detection.RadarShape;
            if (detection.ProcessingTimeMs > existing.MaxProcessingTimeMs)
                existing.MaxProcessingTimeMs = detection.ProcessingTimeMs;
            if (existing.MinProcessingTimeMs == 0 || detection.ProcessingTimeMs < existing.MinProcessingTimeMs)
                existing.MinProcessingTimeMs = detection.ProcessingTimeMs;
            existing.ProcessingTimeHistory.Enqueue(detection.ProcessingTimeMs);
            while (existing.ProcessingTimeHistory.Count > 20) existing.ProcessingTimeHistory.Dequeue();
            existing.ConfidenceHistory.Enqueue(detection.Confidence);
            while (existing.ConfidenceHistory.Count > 20) existing.ConfidenceHistory.Dequeue();

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

            // The is-bot filter now reads THROUGH the fingerprint LFU (the single
            // source), not a stale scalar on the aggregate. A signature whose verdict
            // hasn't been resolved yet (GetResolvedVerdict == null) is treated as
            // not-a-bot for the top-bots list -- it surfaces once ApplyResolvedVerdicts
            // pulls its verdict, the same latency the resolved name already has.
            _sortedCache = _entries
                .Where(kvp => GetResolvedVerdict(kvp.Key)?.IsBot == true)
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
        // Score/verdict scalars are read THROUGH the fingerprint LFU (single source),
        // exactly like the display name. Null until ApplyResolvedVerdicts has populated
        // the entry; a missing verdict projects to 0/null defaults, the same as the
        // name read returns null until resolved.
        var verdict = GetResolvedVerdict(signature);
        lock (agg.SyncRoot)
        {
            return new DashboardTopBotEntry
            {
                PrimarySignature = signature,
                HitCount = agg.HitCount,
                // Display name resolved from the store-gated dict (populated via
                // ApplyResolvedNames from IFingerprintStore.GetDisplayNamesBySignaturesAsync).
                // Null until that read has populated the entry; views must tolerate
                // null and fall through to entity-id / UA-family labels per spec.
                BotName = GetResolvedName(signature),
                CustomBotName = customName,
                BotType = verdict?.BotType,
                RiskBand = verdict?.RiskBand,
                BotProbability = verdict?.BotProbability ?? 0,
                Confidence = verdict?.Confidence ?? 0,
                Action = null,
                CountryCode = agg.CountryCode,
                ProcessingTimeMs = agg.ProcessingTimeMs,
                TopReasons = agg.TopReasons,
                FirstSeen = agg.FirstSeen,
                LastSeen = agg.LastSeen,
                Narrative = agg.Narrative,
                Description = agg.Description,
                IsKnownBot = verdict?.IsBot ?? false,
                ThreatScore = verdict?.ThreatScore,
                ThreatBand = verdict?.ThreatBand,
                IsVerifiedBot = verdict?.IsVerifiedBot ?? false,
                UaFamily = agg.UaFamily,
                UserAgent = agg.UserAgent,
                EntityId = agg.EntityId,
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
    // BotName field DELETED per 2026-06-19-single-source-fingerprint-name spec
    // step 3. The display name lives ONLY in Fingerprint.DisplayName, gated by
    // IFingerprintStore.UpdateDisplayNameForSignatureAsync; view projections
    // fetch it at read-time via IFingerprintStore.GetDisplayNamesBySignaturesAsync.
    // Storing a parallel copy here was the parasitic path that produced
    // "Win Chrome 149" / "Akkoma akkoma..." banned-shape names on staging.
    //
    // Score/verdict scalars (BotType / RiskBand / BotProbability / Confidence /
    // Action / IsBot / ThreatScore / ThreatBand / RiskJustification / IsVerifiedBot)
    // DELETED per the same single-source rule. They are owned by the fingerprint LFU
    // (Fingerprint.CachedBotProbability / CachedRiskBand / InferredClientType /
    // InferredTypeConfidence / ClaimStatus) and read THROUGH it at projection time via
    // SignatureAggregateCache.GetResolvedVerdict / ApplyResolvedVerdicts -- exactly the
    // name read-through pattern. Storing a parallel copy here was the dual-store bug
    // where top-bots showed a stale 0.9 / "Chrome 122" / "Unknown" while the
    // fingerprint store (detail page) showed the fresh value.
    public string? CountryCode;
    public double ProcessingTimeMs;
    public List<string>? TopReasons;

    /// <summary>Lean signed "why" rows (see <see cref="ProjectedVisitor.DetectionReasons"/>) --
    /// projected + capped from DashboardDetectionEvent.DetectorContributions at ingest,
    /// never the raw heavy dict, so this bounded per-fingerprint cache stays lean.</summary>
    public List<DetectionReasonEntry>? DetectionReasons;
    public DateTime FirstSeen;
    public DateTime LastSeen;
    public string? Narrative;
    public string? Description;

    /// <summary>
    ///     UA family (Chrome / Firefox / curl / ...) extracted from the
    ///     detection event's <c>ImportantSignals["ua.family"]</c> signal at
    ///     write time. Drives the composite "{Country} {UaFamily} {Role}"
    ///     label form in <see cref="SignatureDisplayName"/>; without it the
    ///     dashboard rows degrade to "GB User" instead of "GB Chrome User".
    /// </summary>
    public string? UaFamily;

    /// <summary>
    ///     Raw User-Agent string of the latest detection -- feeds the
    ///     dashboard's rich client identity label ("Chrome 119 / macOS")
    ///     via uap-core in <see cref="SignatureDisplayName.Resolve"/>. Without
    ///     this the cache-hit fast path on the gateway short-circuits
    ///     SbVisitorList / Top Bots responses to a null UserAgent and the
    ///     rows degrade back to "GB User N" even after Postgres SELECTs
    ///     started carrying user_agent_raw.
    /// </summary>
    public string? UserAgent;

    /// <summary>
    ///     Durable visitor handle resolved by the gateway via
    ///     <c>IDetectionArchive.ResolveEntityAsync</c> and carried on the
    ///     <c>DashboardDetectionEvent</c>. Latched on the first non-null we
    ///     see and never overwritten with null -- entity ids do not
    ///     "un-allocate" on a subsequent detection that quorum-exited before
    ///     the gateway resolved one. Flows to <see cref="DashboardTopBotEntry.EntityId"/>
    ///     so SbTopBots rows emit the entity-keyed URL instead of falling
    ///     through to the signature URL.
    /// </summary>
    public string? EntityId;

    // ─── Fields moved off ProjectedVisitor in the cache-collapse refactor ────
    // These were duplicated state on VisitorListCache; the visitor card and
    // the signature detail page now both read them off this aggregate so the
    // "two names / two paths at the same instant" regression cannot happen.

    /// <summary>Most recent request path (the row's "Last path" column).</summary>
    public string? LastPath;

    /// <summary>
    ///     Distinct recent paths (capped). Append on every detection if the
    ///     path isn't already present; rotate FIFO when the cap is hit.
    /// </summary>
    public List<string> Paths = new();

    /// <summary>Request id of the most recent detection that wrote to this aggregate.</summary>
    public string? LastRequestId;

    /// <summary>HTTP protocol of the most recent detection (HTTP/1.1 / HTTP/2 / HTTP/3).</summary>
    public string? Protocol;

    /// <summary>HMAC of the visitor's /24 IP subnet -- feeds the behavioural grouper.</summary>
    public string? IpSubnetSignature;

    /// <summary>Metastable fingerprint id from FingerprintMatchContributor.</summary>
    public string? FingerprintId;

    /// <summary>Leiden community cluster id -- feeds the grouper's cluster tier.</summary>
    public string? ClusterId;

    /// <summary>16-dim radar shape vector from the most recent detection.</summary>
    public float[]? RadarShape;

    /// <summary>Maximum processing time observed across all detections for this signature.</summary>
    public double MaxProcessingTimeMs;

    /// <summary>Minimum non-zero processing time observed across all detections for this signature.</summary>
    public double MinProcessingTimeMs;

    /// <summary>
    ///     Ring buffer of recent processing times (last 20 detections) -- powers
    ///     the per-row sparkline column on the visitor card.
    /// </summary>
    public Queue<double> ProcessingTimeHistory = new();

    /// <summary>Ring buffer of recent confidence values (last 20 detections).</summary>
    public Queue<double> ConfidenceHistory = new();

    /// <summary>
    ///     Behavioural grouper's resolved key when this row represents a collapsed
    ///     group (members &gt; 1). Null when standalone or grouper not configured.
    ///     Projected onto group rows by <c>CollapseGroupable</c>.
    /// </summary>
    public Mostlylucid.BotDetection.Grouping.GroupKey? GroupKey;

    /// <summary>Number of member signatures this row represents. 1 when standalone.</summary>
    public int GroupMemberCount = 1;

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

    /// <summary>
    ///     Seed the per-minute ring buffer from an oldest-first 60-element trend
    ///     produced by <see cref="ReadHitTrend"/> on the source (typically the
    ///     gateway's REST envelope). Used by <see cref="SignatureAggregateCache.SeedFromTopBots"/>
    ///     so a remote-mode dashboard host can render the sparkline column from
    ///     the gateway's live ring buffer without having to replay individual
    ///     detection events. Caller must run before the aggregate is published.
    /// </summary>
    internal void SeedHitTrend(int[] oldestFirst)
    {
        if (oldestFirst is null || oldestFirst.Length == 0) return;
        var n = Math.Min(60, oldestFirst.Length);
        for (int i = 0; i < n; i++) _hitsPerMinute[i] = oldestFirst[n - 1 - i];
        // Stamp the bucket to the current UTC minute so ReadHitTrend doesn't
        // bail out on the MinValue sentinel, and so subsequent live RecordHit
        // calls advance the array correctly relative to this seed.
        var now = DateTime.UtcNow;
        _hitsBucketMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
    }

    /// <summary>Sync root for all field mutations.</summary>
    public readonly object SyncRoot = new();
}