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

    public VisitorListCache(int maxVisitors = 100)
    {
        _maxVisitors = maxVisitors;
    }

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
                    LastRequestId = detection.RequestId
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

                    // Push to ring buffers (max 20 entries) — O(1) enqueue/dequeue
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
    {
        IEnumerable<CachedVisitor> items = SnapshotAll();

        items = filter switch
        {
            "humans" => items.Where(v => !v.IsBot),
            "bots" => items.Where(v => v.IsBot),
            "ai" => items.Where(v => v.IsBot && IsAiBot(v)),
            "search" => items.Where(v => v.IsBot && IsSearchBot(v)),
            "tools" => items.Where(v => v.IsBot && IsToolBot(v)),
            _ => items
        };

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

        return items.Take(limit).ToList();
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
                    LastRequestId = v.LastRequestId
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
        if (_visitors.Count <= _maxVisitors) return;

        var toRemove = _visitors
            .OrderBy(kv => kv.Value.LastSeen)
            .Take(_visitors.Count - _maxVisitors)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in toRemove)
            _visitors.TryRemove(key, out _);
    }

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
    ///     Returns (name, type) — either may be null if inference fails.
    /// </summary>
    internal static (string? Name, string? Type) InferBotIdentity(
        IReadOnlyList<string> paths, string? userAgent, int hits, DateTime firstSeen, DateTime lastSeen)
    {
        // 1. Path-based inference — what they're scanning tells us who they are
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

        // 2. UA-based inference — extract identity from user-agent string
        if (!string.IsNullOrEmpty(userAgent))
        {
            var ua = userAgent;
            // AI bots
            if (Regex.IsMatch(ua, @"GPTBot|ChatGPT|CCBot|anthropic-ai|ClaudeBot|Google-Extended|PerplexityBot|Bytespider|Applebot-Extended|cohere-ai|FacebookBot|Meta-ExternalAgent", RegexOptions.IgnoreCase))
                return (ExtractUaBotName(ua) ?? "AI Crawler", "AiBot");
            // Search engines
            if (Regex.IsMatch(ua, @"Googlebot|bingbot|YandexBot|Baiduspider|DuckDuckBot|Slurp|Sogou|Applebot(?!-Extended)", RegexOptions.IgnoreCase))
                return (ExtractUaBotName(ua) ?? "Search Bot", "SearchEngine");
            // SEO/marketing tools
            if (Regex.IsMatch(ua, @"SemrushBot|AhrefsBot|MJ12bot|DotBot|PetalBot|MegaIndex|SerpstatBot|Sistrix|Screaming", RegexOptions.IgnoreCase))
                return (ExtractUaBotName(ua) ?? "SEO Crawler", "Scraper");
            // Monitoring
            if (Regex.IsMatch(ua, @"UptimeRobot|Pingdom|Site24x7|StatusCake|Datadog|NewRelic|GTmetrix|PageSpeed|Lighthouse", RegexOptions.IgnoreCase))
                return (ExtractUaBotName(ua) ?? "Monitor", "MonitoringBot");
            // HTTP libraries
            if (Regex.IsMatch(ua, @"python-requests|python-urllib|python-httpx|aiohttp", RegexOptions.IgnoreCase))
                return ("Python Bot", "Scraper");
            if (Regex.IsMatch(ua, @"^curl/", RegexOptions.IgnoreCase))
                return ("curl", "Scraper");
            if (Regex.IsMatch(ua, @"^wget/", RegexOptions.IgnoreCase))
                return ("wget", "Scraper");
            if (Regex.IsMatch(ua, @"Go-http-client|golang", RegexOptions.IgnoreCase))
                return ("Go Bot", "Scraper");
            if (Regex.IsMatch(ua, @"Java/|Apache-HttpClient|okhttp", RegexOptions.IgnoreCase))
                return ("Java Bot", "Scraper");
            if (Regex.IsMatch(ua, @"node-fetch|axios|undici", RegexOptions.IgnoreCase))
                return ("Node.js Bot", "Scraper");
            if (Regex.IsMatch(ua, @"Ruby|Faraday|Typhoeus", RegexOptions.IgnoreCase))
                return ("Ruby Bot", "Scraper");
            if (Regex.IsMatch(ua, @"PHP/|Guzzle|php-curl", RegexOptions.IgnoreCase))
                return ("PHP Bot", "Scraper");
            if (Regex.IsMatch(ua, @"libwww-perl|LWP|Mechanize", RegexOptions.IgnoreCase))
                return ("Perl Bot", "Scraper");
            if (Regex.IsMatch(ua, @"Scrapy|Nutch|Heritrix", RegexOptions.IgnoreCase))
                return ("Web Crawler", "Scraper");
            // Headless browsers
            if (Regex.IsMatch(ua, @"HeadlessChrome|Headless", RegexOptions.IgnoreCase))
                return ("Headless Chrome", "Scraper");
            if (Regex.IsMatch(ua, @"PhantomJS", RegexOptions.IgnoreCase))
                return ("PhantomJS", "Scraper");
            if (Regex.IsMatch(ua, @"Selenium|WebDriver", RegexOptions.IgnoreCase))
                return ("Selenium Bot", "Scraper");
            if (Regex.IsMatch(ua, @"Playwright", RegexOptions.IgnoreCase))
                return ("Playwright Bot", "Scraper");
            if (Regex.IsMatch(ua, @"Puppeteer", RegexOptions.IgnoreCase))
                return ("Puppeteer Bot", "Scraper");
        }

        // 3. Rate-based inference — high hit rate suggests aggressive bot
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

        // 4. Fallback — we know it's a bot but can't identify it further
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
    /// <summary>Synchronization root — lock before mutating any collection field.</summary>
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

    public string TimeAgo
    {
        get
        {
            var seconds = (int)(DateTime.UtcNow - LastSeen).TotalSeconds;
            if (seconds < 5) return "now";
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m";
            return $"{seconds / 3600}h";
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
