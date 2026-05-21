using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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

    public VisitorListCache(int maxVisitors = 500, SignatureAggregateCache? aggregateCache = null)
    {
        // Default was 100; bumped to 500 so a staging gateway seeing a few hundred
        // distinct signatures over 24h (Amazonbot alone churns 200+ fingerprints)
        // doesn't constantly evict-then-rehydrate the visitor cards. Memory cost
        // per entry is ~1KB so the cap is still well under a megabyte.
        _maxVisitors = maxVisitors;
        // SignatureAggregateCache is the single source of truth for bot name + type
        // (UpdateSignatureBotNameAsync writes to it whenever the canonical naming
        // pipeline fires -- including LLM identification). Without this link, the
        // visitor card showed the initial detection's BotName (often "Unknown Bot")
        // even after the signature was later named, so users saw "Unknown" on the
        // card and the real name on the signature detail page.
        _aggregateCache = aggregateCache;
    }

    // DI-friendly ctor: matches the AddSingleton<VisitorListCache>() registration so
    // SignatureAggregateCache is wired automatically; the maxVisitors-only ctor
    // above is preserved for tests that construct the cache by hand.
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
                var botName = detection.BotName;
                var botType = detection.BotType;

                // Infer bot identity from behavior when the detection ledger didn't provide it
                if (detection.IsBot && string.IsNullOrEmpty(botName))
                {
                    var paths = new List<string> { detection.Path ?? "/" };
                    var (inferredName, inferredType) = InferBotIdentity(
                        paths, detection.UserAgent, 1, detection.Timestamp, detection.Timestamp);
                    botName ??= inferredName;
                    botType ??= inferredType;
                }

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
                    BotName = botName,
                    BotType = botType,
                    CountryCode = detection.CountryCode,
                    UserAgent = detection.UserAgent,
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
                    // Update bot identity: clear stale bot info when detection is now human
                    if (detection.IsBot)
                    {
                        if (!string.IsNullOrEmpty(detection.BotName))
                            existing.BotName = detection.BotName;
                        if (!string.IsNullOrEmpty(detection.BotType))
                            existing.BotType = detection.BotType;

                        // Re-infer identity as more paths accumulate (behavioral refinement)
                        if (string.IsNullOrEmpty(existing.BotName) || existing.BotName == "Unknown Bot")
                        {
                            var (inferredName, inferredType) = InferBotIdentity(
                                existing.Paths, existing.UserAgent, existing.Hits,
                                existing.FirstSeen, existing.LastSeen);
                            if (inferredName != null && inferredName != "Unknown Bot")
                            {
                                existing.BotName = inferredName;
                                existing.BotType ??= inferredType;
                            }
                            else if (existing.BotName == null)
                            {
                                existing.BotName = inferredName;
                                existing.BotType ??= inferredType;
                            }
                        }
                    }
                    else
                    {
                        existing.BotName = null;
                        existing.BotType = null;
                    }
                    if (!string.IsNullOrEmpty(detection.CountryCode))
                        existing.CountryCode = detection.CountryCode;
                    if (!string.IsNullOrEmpty(detection.UserAgent))
                        existing.UserAgent = detection.UserAgent;
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

        // Single source of names: whenever the SignatureAggregateCache has a name
        // for this signature, override the locally-cached one before anything
        // downstream (filter, collapse, sort) reads it. Otherwise the card shows
        // the name captured at first-detection time (often "Unknown Bot") forever,
        // while the signature detail page reads from the aggregate cache and
        // shows the up-to-date canonical name -- the "two different names on the
        // same fingerprint" bug. Mutates the cached row in place; subsequent
        // upserts will keep refreshing it from the same source.
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
    private static IEnumerable<CachedVisitor> CollapseGroupable(IEnumerable<CachedVisitor> source)
    {
        var list = source.ToList();
        foreach (var grp in list.GroupBy(
            v => Middleware.WidgetRenderHelpers.IsGroupableIdentity(customBotName: null, v.BotName, v.BotType)
                 ? "name:" + v.BotName
                 : "sig:" + v.PrimarySignature,
            StringComparer.Ordinal))
        {
            var members = grp.ToList();
            if (members.Count == 1) { yield return members[0]; continue; }

            // Pick the latest-seen as canonical, sum hits, take max bot probability,
            // earliest first-seen across the group.
            var canonical = members.OrderByDescending(v => v.LastSeen).First();
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
                Protocol = canonical.Protocol
            };
        }
    }

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
                });
            }
        }
        return result;
    }

    /// <summary>
    ///     Warm the cache from persisted detection events (e.g. on startup).
    ///     Only populates if the cache is currently empty.
    /// </summary>
    public void WarmFrom(IEnumerable<DashboardDetectionEvent> detections)
    {
        if (!_visitors.IsEmpty) return;

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

    // UA-based bot identity regexes — compiled once, used in hot path per detection.
    private static readonly Regex AiBotUaRegex = new(
        @"GPTBot|ChatGPT|CCBot|anthropic-ai|ClaudeBot|Google-Extended|PerplexityBot|Bytespider|Applebot-Extended|cohere-ai|FacebookBot|Meta-ExternalAgent",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SearchBotUaRegex = new(
        @"Googlebot|bingbot|YandexBot|Baiduspider|DuckDuckBot|Slurp|Sogou|Applebot(?!-Extended)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeoBotUaRegex = new(
        @"SemrushBot|AhrefsBot|MJ12bot|DotBot|PetalBot|MegaIndex|SerpstatBot|Sistrix|Screaming",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MonitorBotUaRegex = new(
        @"UptimeRobot|Pingdom|Site24x7|StatusCake|Datadog|NewRelic|GTmetrix|PageSpeed|Lighthouse",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PythonBotUaRegex = new(
        @"python-requests|python-urllib|python-httpx|aiohttp",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CurlUaRegex = new(@"^curl/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WgetUaRegex = new(@"^wget/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GoBotUaRegex = new(@"Go-http-client|golang", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JavaBotUaRegex = new(@"Java/|Apache-HttpClient|okhttp", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NodeBotUaRegex = new(@"node-fetch|axios|undici", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RubyBotUaRegex = new(@"Ruby|Faraday|Typhoeus", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhpBotUaRegex = new(@"PHP/|Guzzle|php-curl", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerlBotUaRegex = new(@"libwww-perl|LWP|Mechanize", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CrawlerUaRegex = new(@"Scrapy|Nutch|Heritrix", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadlessUaRegex = new(@"HeadlessChrome|Headless", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhantomUaRegex = new(@"PhantomJS", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SeleniumUaRegex = new(@"Selenium|WebDriver", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PlaywrightUaRegex = new(@"Playwright", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PuppeteerUaRegex = new(@"Puppeteer", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex AiNameRegex =
        new(@"\bai\b|gpt|claude|llm|chatbot|copilot|gemini|bard|anthropic|perplexity|cohere",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SearchNameRegex =
        new(@"googlebot|bingbot|yandexbot|baiduspider|duckduckbot|slurp|sogou|exabot|ia_archiver|archive\.org|google|bing",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex ToolNameRegex =
        new(@"semrush|ahrefs|mj12|majestic|screaming|dotbot|petalbot|bytespider|yeti|megaindex|serpstat|sistrix|curl|wget|python|go-http|java|ruby|perl|php|node-fetch|axios|scrapy|httpclient|requests|libwww|lwp|mechanize|webdriver|selenium|playwright|puppeteer|phantom|headless|chrome-lighthouse|pagespeed|gtmetrix|pingdom|uptime|monitor|datadog|newrelic|statuspage",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    ///     Infer the effective bot category from BotType and BotName.
    ///     BotType is often null because the detection ledger only sets it
    ///     when a contribution has ConfidenceDelta > 0. Falling back to BotName
    ///     allows proper categorization for dashboard filters.
    /// </summary>
    private static string InferBotCategory(string? botType, string? botName)
    {
        // Explicit BotType takes precedence
        if (!string.IsNullOrEmpty(botType))
        {
            if (botType is "AiBot") return "ai";
            if (botType is "SearchEngine" or "VerifiedBot" or "GoodBot") return "search";
            if (botType is "Scraper" or "MonitoringBot" or "SocialMediaBot") return "tools";
            return "other";
        }

        // Infer from BotName when BotType is null
        if (!string.IsNullOrEmpty(botName))
        {
            if (AiNameRegex.IsMatch(botName)) return "ai";
            if (SearchNameRegex.IsMatch(botName)) return "search";
            if (ToolNameRegex.IsMatch(botName)) return "tools";
        }

        return "other";
    }

    /// <summary>
    ///     Infer bot name and type from behavioral signals when the detection ledger
    ///     didn't provide them. Uses paths visited, user-agent, and hit rate.
    ///     Returns (name, type) - either may be null if inference fails.
    /// </summary>
    internal static (string? Name, string? Type) InferBotIdentity(
        IReadOnlyList<string> paths, string? userAgent, int hits, DateTime firstSeen, DateTime lastSeen)
    {
        // 1. Path-based inference - what they're scanning tells us who they are
        var pathSet = string.Join(" ", paths).ToLowerInvariant();

        if (WpPathRegex.IsMatch(pathSet))
            return ("WordPress Scanner", "Scraper");
        if (ConfigPathRegex.IsMatch(pathSet))
            return ("Config Scanner", "Scraper");
        if (ExploitPathRegex.IsMatch(pathSet))
            return ("Exploit Scanner", "Scraper");
        if (DbPathRegex.IsMatch(pathSet))
            return ("Database Scanner", "Scraper");
        if (ApiPathRegex.IsMatch(pathSet))
            return ("API Prober", "Scraper");
        if (CmsPathRegex.IsMatch(pathSet))
            return ("CMS Scanner", "Scraper");

        // 2. UA-based inference - extract identity from user-agent string
        if (!string.IsNullOrEmpty(userAgent))
        {
            var ua = userAgent;
            if (AiBotUaRegex.IsMatch(ua))
                return (ExtractUaBotName(ua) ?? "AI Crawler", "AiBot");
            if (SearchBotUaRegex.IsMatch(ua))
                return (ExtractUaBotName(ua) ?? "Search Bot", "SearchEngine");
            if (SeoBotUaRegex.IsMatch(ua))
                return (ExtractUaBotName(ua) ?? "SEO Crawler", "Scraper");
            if (MonitorBotUaRegex.IsMatch(ua))
                return (ExtractUaBotName(ua) ?? "Monitor", "MonitoringBot");
            if (PythonBotUaRegex.IsMatch(ua))
                return ("Python Bot", "Scraper");
            if (CurlUaRegex.IsMatch(ua))
                return ("curl", "Scraper");
            if (WgetUaRegex.IsMatch(ua))
                return ("wget", "Scraper");
            if (GoBotUaRegex.IsMatch(ua))
                return ("Go Bot", "Scraper");
            if (JavaBotUaRegex.IsMatch(ua))
                return ("Java Bot", "Scraper");
            if (NodeBotUaRegex.IsMatch(ua))
                return ("Node.js Bot", "Scraper");
            if (RubyBotUaRegex.IsMatch(ua))
                return ("Ruby Bot", "Scraper");
            if (PhpBotUaRegex.IsMatch(ua))
                return ("PHP Bot", "Scraper");
            if (PerlBotUaRegex.IsMatch(ua))
                return ("Perl Bot", "Scraper");
            if (CrawlerUaRegex.IsMatch(ua))
                return ("Web Crawler", "Scraper");
            if (HeadlessUaRegex.IsMatch(ua))
                return ("Headless Chrome", "Scraper");
            if (PhantomUaRegex.IsMatch(ua))
                return ("PhantomJS", "Scraper");
            if (SeleniumUaRegex.IsMatch(ua))
                return ("Selenium Bot", "Scraper");
            if (PlaywrightUaRegex.IsMatch(ua))
                return ("Playwright Bot", "Scraper");
            if (PuppeteerUaRegex.IsMatch(ua))
                return ("Puppeteer Bot", "Scraper");
        }

        // 3. Rate-based inference - high hit rate suggests aggressive bot
        if (hits > 10 && lastSeen > firstSeen)
        {
            var seconds = (lastSeen - firstSeen).TotalSeconds;
            if (seconds > 0)
            {
                var rpm = hits / seconds * 60.0;
                if (rpm > 60)
                    return ("Aggressive Crawler", "Scraper");
                if (rpm > 20)
                    return ("Fast Crawler", "Scraper");
            }
        }

        // 4. Fallback - we know it's a bot but can't identify it further
        return ("Unknown Bot", null);
    }

    /// <summary>
    ///     Extract a clean bot name from a user-agent string.
    ///     E.g. "Mozilla/5.0 (compatible; GPTBot/1.0)" → "GPTBot"
    /// </summary>
    private static string? ExtractUaBotName(string ua)
    {
        // Try "compatible; BotName/version" pattern
        var m = Regex.Match(ua, @"compatible;\s*([A-Za-z][\w-]+)", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        // Try "BotName/version" at start
        m = Regex.Match(ua, @"^([A-Za-z][\w-]+)/[\d.]", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return null;
    }

    // Path pattern regexes for behavioral inference
    private static readonly Regex WpPathRegex = new(
        @"wp-admin|wp-login|wp-content|wp-includes|xmlrpc\.php|wp-json|wp-cron",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConfigPathRegex = new(
        @"\.env|\.git|\.aws|\.ssh|\.config|\.htaccess|\.htpasswd|web\.config|appsettings|credentials|\.key|\.pem|\.bak",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExploitPathRegex = new(
        @"/shell|/cmd|/eval|/exec|cgi-bin|/setup|phpunit|vendor/phpunit|/debug|/console|actuator|/solr|struts|/ognl|ThinkPHP",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DbPathRegex = new(
        @"phpmyadmin|/pma|/mysql|/adminer|/dbadmin|/sql|/pgadmin|/mongodb",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApiPathRegex = new(
        @"/graphql|/swagger|/openapi|/api-docs|/v1/|/v2/|/rest/",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CmsPathRegex = new(
        @"/administrator|/joomla|/drupal|/magento|/shopify|/typo3|/umbraco|/sitecore|/craft",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? ExtractProtocol(DashboardDetectionEvent detection)
    {
        if (detection.ImportantSignals == null) return null;
        if (detection.ImportantSignals.TryGetValue("request.protocol", out var proto))
            return proto?.ToString();
        if (detection.ImportantSignals.ContainsKey("h3.protocol")) return "HTTP/3";
        if (detection.ImportantSignals.ContainsKey("h2.protocol")) return "HTTP/2";
        return null;
    }

    private static bool IsAiBot(CachedVisitor v)
    {
        var cat = InferBotCategory(v.BotType, v.BotName);
        if (cat == "ai") return true;
        // Also check UA for AI bots when category fell through
        if (!string.IsNullOrEmpty(v.UserAgent) && Regex.IsMatch(v.UserAgent,
                @"GPTBot|ChatGPT|CCBot|anthropic-ai|ClaudeBot|Google-Extended|PerplexityBot|Applebot-Extended|cohere-ai|Meta-ExternalAgent",
                RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    private static bool IsSearchBot(CachedVisitor v)
    {
        var cat = InferBotCategory(v.BotType, v.BotName);
        return cat == "search";
    }

    private static bool IsToolBot(CachedVisitor v)
    {
        var cat = InferBotCategory(v.BotType, v.BotName);
        return cat == "tools";
    }

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