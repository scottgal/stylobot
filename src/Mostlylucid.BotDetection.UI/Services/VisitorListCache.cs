using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Grouping;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Singleton that maintains a server-side cache of the latest visitors.
///     Updated by DetectionBroadcastMiddleware after each detection.
///     Provides filtered, sorted lists for HTMX rendering (same for all clients).
/// </summary>
public class VisitorListCache
{
    private readonly ConcurrentDictionary<string, CachedVisitor> _visitors = new();
    private readonly int _maxVisitors;
    private readonly SignatureAggregateCache? _aggregateCache;
    private readonly IBehaviouralGrouper? _grouper;

    public VisitorListCache(
        int maxVisitors = 500,
        SignatureAggregateCache? aggregateCache = null,
        IBehaviouralGrouper? grouper = null)
    {
        // Default was 100; bumped to 500 so a staging gateway seeing a few hundred
        // distinct signatures over 24h (Amazonbot alone churns 200+ fingerprints)
        // doesn't constantly evict-then-rehydrate the visitor cards. Memory cost
        // per entry is ~1KB so the cap is still well under a megabyte.
        _maxVisitors = maxVisitors;
        _aggregateCache = aggregateCache;
        // Behavioural grouper -- single bridge that decides how rows
        // collapse. Optional so legacy test constructors still work; in
        // production it's wired by AddBotDetection.
        _grouper = grouper;
    }

    // DI-friendly ctor: matches the AddSingleton<VisitorListCache>() registration so
    // SignatureAggregateCache + IBehaviouralGrouper are wired automatically; the
    // maxVisitors-only ctor above is preserved for tests that construct by hand.
    public VisitorListCache(SignatureAggregateCache aggregateCache, IBehaviouralGrouper grouper)
        : this(500, aggregateCache, grouper) { }

    // Back-compat ctor for tests that only pass the aggregate cache.
    public VisitorListCache(SignatureAggregateCache aggregateCache) : this(500, aggregateCache) { }

    /// <summary>
    ///     Upsert a visitor from a detection event.
    ///     Called by DetectionBroadcastMiddleware after each detection.
    /// </summary>
    public CachedVisitor Upsert(DashboardDetectionEvent detection)
    {
        var sig = detection.PrimarySignature;
        if (string.IsNullOrEmpty(sig))
            sig = detection.RequestId;

        var visitor = _visitors.AddOrUpdate(sig,
            _ =>
            {
                // BotName / BotType come straight from the detection event. Any later
                // canonical naming (LLM resolves a description, UA pattern catches up,
                // operator labels a fingerprint) updates SignatureAggregateCache via
                // ApplyBotName, and GetFiltered overrides the cached card from that
                // source on every render -- so the value stored on the row is only a
                // snapshot. No inline regex inference here.
                return new CachedVisitor
                {
                    PrimarySignature = sig,
                    Hits = 1,
                    FirstSeen = detection.Timestamp,
                    LastSeen = detection.Timestamp,
                    IsBot = detection.IsBot,
                    BotProbability = detection.BotProbability,
                    Confidence = detection.Confidence,
                    RiskBand = detection.RiskBand ?? "Medium",
                    LastPath = detection.Path,
                    Paths = new List<string> { detection.Path ?? "/" },
                    Action = detection.Action ?? "Allow",
                    BotName = detection.BotName,
                    BotType = detection.BotType,
                    CountryCode = detection.CountryCode,
                    // DashboardDetectionEvent carries two UA fields: UserAgent (set by
                    // the live detection pipeline) and UserAgentRaw (set by SQLite warmup
                    // from the user_agent_raw column). Warmup-loaded events only have the
                    // latter, so prefer it when present; otherwise fall back to the live
                    // field. Without this coalesce, warmed rows show "-" in the UA column.
                    UserAgent = detection.UserAgentRaw ?? detection.UserAgent,
                    Narrative = detection.Narrative,
                    Description = detection.Description,
                    TopReasons = detection.TopReasons.ToList(),
                    ProcessingTimeMs = detection.ProcessingTimeMs,
                    MaxProcessingTimeMs = detection.ProcessingTimeMs,
                    MinProcessingTimeMs = detection.ProcessingTimeMs,
                    ProcessingTimeHistory = new Queue<double>([detection.ProcessingTimeMs]),
                    BotProbabilityHistory = new Queue<double>([detection.BotProbability]),
                    ConfidenceHistory = new Queue<double>([detection.Confidence]),
                    LastRequestId = detection.RequestId,
                    ThreatScore = detection.ThreatScore,
                    ThreatBand = detection.ThreatBand,
                    Protocol = ExtractProtocol(detection),
                    IpSubnetSignature = ExtractSignal(detection, "ip.subnet"),
                    // ua.family in RawSignals is set by the live detection pipeline.
                    // Warmup-loaded events have an empty RawSignals dict; derive the
                    // family from the stored UA string in that case so the Visitors
                    // table's "UA" column is populated for warmed rows too.
                    UaFamily = ExtractSignal(detection, "ua.family")
                        ?? DeriveUaFamily(detection.UserAgentRaw ?? detection.UserAgent),
                    FingerprintId = ExtractSignal(detection, "identity.fingerprint_id"),
                    ClusterId = ExtractSignal(detection, "cluster.id"),
                    RadarShape = detection.RadarShape,
                };
            },
            (_, existing) =>
            {
                lock (existing.SyncRoot)
                {
                    existing.Hits++;
                    existing.LastSeen = detection.Timestamp;
                    existing.IsBot = detection.IsBot;
                    existing.BotProbability = detection.BotProbability;
                    existing.Confidence = detection.Confidence;
                    existing.RiskBand = detection.RiskBand ?? existing.RiskBand;
                    existing.LastPath = detection.Path;
                    existing.Action = detection.Action ?? existing.Action;
                    if (!string.IsNullOrEmpty(detection.Narrative))
                        existing.Narrative = detection.Narrative;
                    if (!string.IsNullOrEmpty(detection.Description))
                        existing.Description = detection.Description;
                    if (detection.TopReasons.Count > 0)
                        existing.TopReasons = detection.TopReasons.ToList();
                    // Update bot identity from the detection event only. The regex
                    // re-inference path used to fire here ("Unknown Bot" -> guess from
                    // paths + UA via InferBotIdentity) is gone -- SignatureAggregateCache
                    // is now the canonical name source, GetFiltered overrides the row
                    // before rendering. The inline regex was a parallel naming pipeline
                    // that silently drifted from the YAML catalog.
                    if (detection.IsBot)
                    {
                        if (!string.IsNullOrEmpty(detection.BotName))
                            existing.BotName = detection.BotName;
                        if (!string.IsNullOrEmpty(detection.BotType))
                            existing.BotType = detection.BotType;
                    }
                    else
                    {
                        existing.BotName = null;
                        existing.BotType = null;
                    }
                    if (!string.IsNullOrEmpty(detection.CountryCode))
                        existing.CountryCode = detection.CountryCode;
                    // Coalesce both UA sources -- live pipeline fills UserAgent, warmup
                    // fills UserAgentRaw. Same defensive read as the create branch above.
                    var incomingUa = detection.UserAgentRaw ?? detection.UserAgent;
                    if (!string.IsNullOrEmpty(incomingUa))
                        existing.UserAgent = incomingUa;
                    existing.ProcessingTimeMs = detection.ProcessingTimeMs;
                    if (detection.ProcessingTimeMs > existing.MaxProcessingTimeMs)
                        existing.MaxProcessingTimeMs = detection.ProcessingTimeMs;
                    if (detection.ProcessingTimeMs < existing.MinProcessingTimeMs || existing.MinProcessingTimeMs == 0)
                        existing.MinProcessingTimeMs = detection.ProcessingTimeMs;

                    // Push to ring buffers (max 20 entries) - O(1) enqueue/dequeue
                    existing.ProcessingTimeHistory.Enqueue(detection.ProcessingTimeMs);
                    if (existing.ProcessingTimeHistory.Count > 20)
                        existing.ProcessingTimeHistory.Dequeue();
                    existing.BotProbabilityHistory.Enqueue(detection.BotProbability);
                    if (existing.BotProbabilityHistory.Count > 20)
                        existing.BotProbabilityHistory.Dequeue();
                    existing.ConfidenceHistory.Enqueue(detection.Confidence);
                    if (existing.ConfidenceHistory.Count > 20)
                        existing.ConfidenceHistory.Dequeue();

                    existing.LastRequestId = detection.RequestId;
                    existing.ThreatScore = detection.ThreatScore ?? existing.ThreatScore;
                    existing.ThreatBand = detection.ThreatBand ?? existing.ThreatBand;
                    var proto = ExtractProtocol(detection);
                    if (!string.IsNullOrEmpty(proto))
                        existing.Protocol = proto;
                    var subnet = ExtractSignal(detection, "ip.subnet");
                    if (!string.IsNullOrEmpty(subnet))
                        existing.IpSubnetSignature = subnet;
                    var uaFamily = ExtractSignal(detection, "ua.family")
                        ?? DeriveUaFamily(incomingUa);
                    if (!string.IsNullOrEmpty(uaFamily))
                        existing.UaFamily = uaFamily;
                    if (detection.RadarShape is { Length: 16 })
                        existing.RadarShape = detection.RadarShape;
                    var fpid = ExtractSignal(detection, "identity.fingerprint_id");
                    if (!string.IsNullOrEmpty(fpid))
                        existing.FingerprintId = fpid;
                    var clusterId = ExtractSignal(detection, "cluster.id");
                    if (!string.IsNullOrEmpty(clusterId))
                        existing.ClusterId = clusterId;
                    if (!string.IsNullOrEmpty(detection.Path) && !existing.Paths.Contains(detection.Path))
                    {
                        existing.Paths.Add(detection.Path);
                        if (existing.Paths.Count > 20)
                            existing.Paths.RemoveAt(0);
                    }
                }
                return existing;
            });

        EvictOldest();
        return visitor;
    }

    /// <summary>
    ///     Get filtered, sorted, sliced list for HTMX rendering.
    ///     Takes snapshots of mutable fields under lock for thread safety.
    /// </summary>
    public IReadOnlyList<CachedVisitor> GetFiltered(string? filter, string sortField, string sortDir, int limit = 50)
        => GetFiltered(filter, sortField, sortDir, page: 1, pageSize: limit).Items;

    /// <summary>
    ///     Get filtered, sorted, paginated list for HTMX rendering.
    ///     Returns items for the requested page plus total filtered count for pagination.
    /// </summary>
    public (IReadOnlyList<CachedVisitor> Items, int TotalCount, int Page, int PageSize) GetFiltered(
        string? filter, string sortField, string sortDir, int page, int pageSize)
    {
        var snapshot = SnapshotAll();

        // Single source of truth: SignatureAggregateCache owns bot_name, bot_type,
        // risk_band, threat_band, bot_probability for every signature on every
        // dashboard surface. The visitor cache stores a copy purely as an event-time
        // snapshot; the canonical values may have been updated since (LLM naming
        // landed, a newer detection escalated the band, the YAML overrode a
        // heuristic guess), and the signature detail page reads from the aggregate
        // cache. If the visitor card kept the snapshot value the same fingerprint
        // would show one band/name on the card and another on the detail -- the
        // class-of-bug the operator has hit repeatedly. Override in place before
        // filter / collapse / sort runs so every downstream consumer sees the
        // canonical values.
        if (_aggregateCache != null)
        {
            foreach (var v in snapshot)
            {
                if (string.IsNullOrEmpty(v.PrimarySignature)) continue;
                if (!_aggregateCache.TryGet(v.PrimarySignature, out var agg) || agg == null) continue;
                if (!string.IsNullOrEmpty(agg.BotName) && agg.BotName != v.BotName)
                    v.BotName = agg.BotName;
                if (!string.IsNullOrEmpty(agg.BotType) && agg.BotType != v.BotType)
                    v.BotType = agg.BotType;
                if (!string.IsNullOrEmpty(agg.RiskBand) && agg.RiskBand != v.RiskBand)
                    v.RiskBand = agg.RiskBand;
                if (!string.IsNullOrEmpty(agg.ThreatBand) && agg.ThreatBand != v.ThreatBand)
                    v.ThreatBand = agg.ThreatBand;
            }
        }

        IEnumerable<CachedVisitor> items = snapshot;

        items = filter switch
        {
            "humans" => items.Where(v => !v.IsBot),
            "bots" => items.Where(v => v.IsBot),
            "ai" => items.Where(v => v.IsBot && IsAiBot(v)),
            "search" => items.Where(v => v.IsBot && IsSearchBot(v)),
            "tools" => items.Where(v => v.IsBot && IsToolBot(v)),
            _ => items
        };

        // Same grouping rule used for Top Bots / Live Activity: rows that share a
        // verified-bot identity name (Amazonbot / Googlebot / ClaudeBot etc.) collapse
        // into one aggregate, summing Hits across the group, keeping the latest-seen
        // canonical row. Without this, the matcher converging 30 source IPs onto the
        // Amazonbot identity rendered as 30 separate "Amazonbot Hits: 1" cards. The
        // groupable predicate lives in WidgetRenderHelpers -- one source of truth used
        // by both this surface and SbTopBots/Default.cshtml.
        items = CollapseGroupable(items);

        items = (sortField, sortDir) switch
        {
            ("name", "asc") => items.OrderBy(v => v.BotName ?? v.PrimarySignature),
            ("name", _) => items.OrderByDescending(v => v.BotName ?? v.PrimarySignature),
            ("hits", "asc") => items.OrderBy(v => v.Hits),
            ("hits", _) => items.OrderByDescending(v => v.Hits),
            ("risk", "asc") => items.OrderBy(v => RiskOrder(v.RiskBand)),
            ("risk", _) => items.OrderByDescending(v => RiskOrder(v.RiskBand)),
            (_, "asc") => items.OrderBy(v => v.LastSeen),
            _ => items.OrderByDescending(v => v.LastSeen)
        };

        var materialized = items.ToList();
        var totalCount = materialized.Count;
        var paged = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (paged, totalCount, page, pageSize);
    }

    /// <summary>
    ///     Get a single visitor by signature.
    /// </summary>
    public CachedVisitor? Get(string primarySignature)
    {
        return _visitors.TryGetValue(primarySignature, out var v) ? v : null;
    }

    /// <summary>
    ///     Get filter badge counts.
    /// </summary>
    public FilterCounts GetCounts()
    {
        var all = SnapshotAll();
        return new FilterCounts
        {
            All = all.Count,
            Humans = all.Count(v => !v.IsBot),
            Bots = all.Count(v => v.IsBot),
            Ai = all.Count(v => v.IsBot && IsAiBot(v)),
            Search = all.Count(v => v.IsBot && IsSearchBot(v)),
            Tools = all.Count(v => v.IsBot && IsToolBot(v))
        };
    }

    /// <summary>
    ///     Collapse multiple rows that share a verified-bot identity (Amazonbot,
    ///     Googlebot, ClaudeBot, etc.) into one aggregate per name. The groupable
    ///     predicate is shared with SbTopBots via
    ///     <see cref="Middleware.WidgetRenderHelpers.IsGroupableIdentity(string?,string?,string?)"/>
    ///     so the visitor card list and the Top Bots list always agree on what
    ///     counts as the same identity.
    /// </summary>
    /// <summary>
    ///     Build the collapse key for a visitor row. When the behavioural
    ///     grouper is wired (production), defers to it. Otherwise falls
    ///     back to the legacy bot-name-or-signature rule so test
    ///     constructors that don't inject a grouper still behave correctly.
    /// </summary>
    private string ResolveGroupCanonical(CachedVisitor v)
    {
        if (_grouper is not null)
        {
            return _grouper.Resolve(BuildInput(v)).Canonical;
        }

        // Legacy fallback.
        return Middleware.WidgetRenderHelpers.IsGroupableIdentity(
                   customBotName: null, v.BotName, v.BotType)
            ? "name:" + v.BotName
            : "sig:" + v.PrimarySignature;
    }

    private IEnumerable<CachedVisitor> CollapseGroupable(IEnumerable<CachedVisitor> source)
    {
        var list = source.ToList();
        foreach (var grp in list.GroupBy(v => ResolveGroupCanonical(v), StringComparer.Ordinal))
        {
            var members = grp.ToList();
            if (members.Count == 1) { yield return members[0]; continue; }

            // Pick the latest-seen as canonical, sum hits, take max bot probability,
            // earliest first-seen across the group.
            var canonical = members.OrderByDescending(v => v.LastSeen).First();
            // Resolve the grouper's key for the canonical row so the dashboard
            // can render the source + reason chip ("Cluster c47 (23 members)" etc).
            // Only attached when the row actually represents > 1 signature.
            var resolvedKey = _grouper?.Resolve(BuildInput(canonical));
            yield return new CachedVisitor
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
                ProcessingTimeMs = canonical.ProcessingTimeMs,
                MaxProcessingTimeMs = members.Max(v => v.MaxProcessingTimeMs),
                MinProcessingTimeMs = members.Min(v => v.MinProcessingTimeMs > 0 ? v.MinProcessingTimeMs : double.MaxValue),
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

    /// <summary>
    ///     Build a <see cref="Mostlylucid.BotDetection.Grouping.GroupingInput"/>
    ///     from a cached visitor. Centralised so the canonical resolve in
    ///     <see cref="CollapseGroupable"/> and the per-row resolve in
    ///     <see cref="ResolveGroupCanonical"/> stay in sync.
    /// </summary>
    private static Mostlylucid.BotDetection.Grouping.GroupingInput BuildInput(CachedVisitor v) => new()
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

    /// <summary>
    ///     Get top N bots by hit count.
    /// </summary>
    public IReadOnlyList<CachedVisitor> GetTopBots(int count = 5)
    {
        return SnapshotAll()
            .Where(v => v.IsBot)
            .OrderByDescending(v => v.Hits)
            .Take(count)
            .ToList();
    }

    /// <summary>
    ///     Take a thread-safe snapshot of all visitors.
    ///     Reads mutable fields under SyncRoot to avoid torn reads.
    /// </summary>
    private List<CachedVisitor> SnapshotAll()
    {
        var result = new List<CachedVisitor>(_visitors.Count);
        foreach (var kv in _visitors)
        {
            var v = kv.Value;
            lock (v.SyncRoot)
            {
                // Shallow copy with snapshot of current values
                result.Add(new CachedVisitor
                {
                    PrimarySignature = v.PrimarySignature,
                    Hits = v.Hits,
                    FirstSeen = v.FirstSeen,
                    LastSeen = v.LastSeen,
                    IsBot = v.IsBot,
                    BotProbability = v.BotProbability,
                    Confidence = v.Confidence,
                    RiskBand = v.RiskBand,
                    LastPath = v.LastPath,
                    Paths = v.Paths.ToList(),
                    Action = v.Action,
                    BotName = v.BotName,
                    BotType = v.BotType,
                    CountryCode = v.CountryCode,
                    UserAgent = v.UserAgent,
                    Narrative = v.Narrative,
                    Description = v.Description,
                    TopReasons = v.TopReasons.ToList(),
                    ProcessingTimeMs = v.ProcessingTimeMs,
                    MaxProcessingTimeMs = v.MaxProcessingTimeMs,
                    MinProcessingTimeMs = v.MinProcessingTimeMs,
                    ProcessingTimeHistory = new Queue<double>(v.ProcessingTimeHistory),
                    BotProbabilityHistory = new Queue<double>(v.BotProbabilityHistory),
                    ConfidenceHistory = new Queue<double>(v.ConfidenceHistory),
                    LastRequestId = v.LastRequestId,
                    ThreatScore = v.ThreatScore,
                    ThreatBand = v.ThreatBand,
                    Protocol = v.Protocol,
                    // Identity / fingerprint fields. Previously omitted -- the snapshot
                    // built a CachedVisitor without these and every consumer that read
                    // through SnapshotAll (GetFiltered, CollapseGroupable, the visitor
                    // table) saw nulls regardless of what Upsert had assigned. That's
                    // why the Visitors table's UA column rendered "-" even with
                    // user_agent_raw flowing all the way through Postgres into Upsert.
                    UaFamily = v.UaFamily,
                    IpSubnetSignature = v.IpSubnetSignature,
                    FingerprintId = v.FingerprintId,
                    ClusterId = v.ClusterId,
                    RadarShape = v.RadarShape,
                });
            }
        }
        return result;
    }

    /// <summary>
    ///     Warm the cache from persisted detection events (e.g. on startup).
    ///     Idempotent: Upsert handles existing entries via AddOrUpdate, so calling
    ///     this when the cache already has live entries merges rather than skips.
    ///     <para>
    ///     Previously this method returned early when <c>!_visitors.IsEmpty</c>.
    ///     Combined with the warmup service's 2-second startup delay, a single
    ///     live request arriving in that window populated the cache with one
    ///     entry and the guard skipped the warm entirely -- staging routinely
    ///     ended up with VisitorListCache=1 while SignatureAggregateCache
    ///     warmed correctly to 90+. Removing the guard restores symmetry.
    ///     </para>
    /// </summary>
    public void WarmFrom(IEnumerable<DashboardDetectionEvent> detections)
    {
        foreach (var detection in detections)
            Upsert(detection);
    }

    private void EvictOldest()
    {
        // Only evict when 10% over capacity to amortize the O(n log n) sort cost.
        var overage = _visitors.Count - _maxVisitors;
        if (overage <= _maxVisitors / 10) return;

        var toRemove = _visitors
            .OrderBy(kv => kv.Value.LastSeen)
            .Take(overage)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _visitors.TryRemove(key, out _);
    }

    // Bot identity is owned by the YAML pattern loader + SignatureAggregateCache.
    // VisitorListCache used to hold a parallel regex farm (UA/path/name detectors)
    // for "infer bot identity when the detection ledger didn't populate it." That
    // surface has been removed -- GetFiltered overrides BotName / BotType /
    // RiskBand / ThreatBand from SignatureAggregateCache at render time, so any
    // detection event that arrives with a null name will pick up the canonical
    // one on the next render anyway. The path-pattern regexes (WpPathRegex,
    // ConfigPathRegex, etc.) were also dead weight -- BotPatternLoader covers
    // UA-based identity, IntentContributor covers path-based intent. Both lived
    // here as a duplicate that silently drifted from the YAML catalog.

    private static string? ExtractProtocol(DashboardDetectionEvent detection)
    {
        if (detection.ImportantSignals == null) return null;
        if (detection.ImportantSignals.TryGetValue("request.protocol", out var proto))
            return proto?.ToString();
        if (detection.ImportantSignals.ContainsKey("h3.protocol")) return "HTTP/3";
        if (detection.ImportantSignals.ContainsKey("h2.protocol")) return "HTTP/2";
        return null;
    }

    /// <summary>
    ///     Read a single signal value off the detection event by key.
    ///     Used to populate behavioural-grouper inputs (ip.subnet,
    ///     ua.family) onto the cached visitor without expanding the model
    ///     surface area.
    /// </summary>
    private static string? ExtractSignal(DashboardDetectionEvent detection, string key)
    {
        if (detection.ImportantSignals is null) return null;
        if (detection.ImportantSignals.TryGetValue(key, out var v) && v is not null)
            return v.ToString();
        return null;
    }

    /// <summary>
    ///     Parse the UA family ("Chrome", "Safari", "curl", "Googlebot", ...) out of
    ///     the raw UA string when the live pipeline's ua.family signal isn't present.
    ///     Used for warmup-loaded events whose ImportantSignals dictionary is empty.
    ///     <para>
    ///     Internal -- <see cref="SignatureAggregateCache.WarmFromDetections"/> reuses
    ///     this so the warmed aggregate's UaFamily matches the visitor cache's value
    ///     for the same signature instead of decoupling.
    ///     </para>
    /// </summary>
    internal static string? DeriveUaFamily(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var family = Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(userAgent).Family;
        return string.IsNullOrEmpty(family) ? null : family;
    }

    // Filter categories key off BotType. BotType is the authoritative classification
    // from the YAML catalog (UserAgentContributor matches the UA against the loaded
    // patterns and writes the bot_type into the signal), and GetFiltered keeps it in
    // sync from SignatureAggregateCache on every render. No name-regex fallback --
    // anything not classified in the catalog falls into "other" and is hidden from
    // these filters rather than guessed.
    private static bool IsAiBot(CachedVisitor v) => v.BotType is "AiBot";
    private static bool IsSearchBot(CachedVisitor v) => v.BotType is "SearchEngine" or "VerifiedBot" or "GoodBot";
    private static bool IsToolBot(CachedVisitor v) => v.BotType is "Scraper" or "MonitoringBot" or "SocialMediaBot" or "Tool";

    private static int RiskOrder(string? band) => band switch
    {
        "VeryHigh" => 5, "High" => 4, "Medium" or "Elevated" => 3, "Low" => 2, "VeryLow" => 1, _ => 0
    };
}

/// <summary>
///     A cached visitor entry for HTMX rendering.
/// </summary>
public class CachedVisitor
{
    /// <summary>Synchronization root - lock before mutating any collection field.</summary>
    internal readonly object SyncRoot = new();

    public required string PrimarySignature { get; set; }
    public int Hits { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public double Confidence { get; set; }
    public string RiskBand { get; set; } = "Medium";
    public string? LastPath { get; set; }
    public List<string> Paths { get; set; } = new();
    public string Action { get; set; } = "Allow";
    public string? BotName { get; set; }
    public string? BotType { get; set; }
    public string? CountryCode { get; set; }
    public string? UserAgent { get; set; }
    public string? Narrative { get; set; }
    public string? Description { get; set; }
    public List<string> TopReasons { get; set; } = new();
    public double ProcessingTimeMs { get; set; }
    public double MaxProcessingTimeMs { get; set; }
    public double MinProcessingTimeMs { get; set; }

    /// <summary>Ring buffer of recent processing times (last 20 requests) for sparkline.</summary>
    public Queue<double> ProcessingTimeHistory { get; set; } = new();
    /// <summary>Ring buffer of recent bot probabilities (last 20 requests) for sparkline.</summary>
    public Queue<double> BotProbabilityHistory { get; set; } = new();
    /// <summary>Ring buffer of recent confidence values (last 20 requests) for sparkline.</summary>
    public Queue<double> ConfidenceHistory { get; set; } = new();

    public string? LastRequestId { get; set; }
    public double? ThreatScore { get; set; }
    public string? ThreatBand { get; set; }
    public string? Protocol { get; set; }

    /// <summary>HMAC of the visitor's /24 subnet -- feeds the behavioural grouper's subnet-rotation tier.</summary>
    public string? IpSubnetSignature { get; set; }

    /// <summary>User-agent family (e.g. "Chrome", "Safari", "curl") -- feeds the rotation tier's UA diversity check.</summary>
    public string? UaFamily { get; set; }

    /// <summary>Metastable fingerprint id from FingerprintMatchContributor -- feeds the grouper's identity tier.</summary>
    public string? FingerprintId { get; set; }

    /// <summary>Leiden community cluster id -- feeds the grouper's cluster tier.</summary>
    public string? ClusterId { get; set; }

    /// <summary>16-dim radar shape vector from the latest detection. Used by the Your
    /// Detection widget to show the visitor's own fingerprint as a mini clock radar.</summary>
    public float[]? RadarShape { get; set; }

    /// <summary>
    ///     The behavioural grouper's resolved key for this row. Set by
    ///     <c>CollapseGroupable</c> when this row represents a collapsed
    ///     group (members > 1); null when the row stands alone or when
    ///     the grouper is disabled. The view renders a chip from this.
    /// </summary>
    public Mostlylucid.BotDetection.Grouping.GroupKey? GroupKey { get; set; }

    /// <summary>Number of member signatures this row represents. 1 when standalone.</summary>
    public int GroupMemberCount { get; set; } = 1;

    public string TimeAgo
    {
        get
        {
            var span = DateTime.UtcNow - LastSeen;
            if (span.TotalSeconds < 5) return "now";
            if (span.TotalSeconds < 60) return $"{(int)span.TotalSeconds}s";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
            return $"{(int)span.TotalDays}d";
        }
    }
}

/// <summary>
///     Filter button badge counts.
/// </summary>
public class FilterCounts
{
    public int All { get; set; }
    public int Humans { get; set; }
    public int Bots { get; set; }
    public int Ai { get; set; }
    public int Search { get; set; }
    public int Tools { get; set; }
}