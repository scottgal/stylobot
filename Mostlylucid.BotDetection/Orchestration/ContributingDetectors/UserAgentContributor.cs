using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     User-Agent based bot detection.
///     Runs in the first wave (no dependencies).
///     Emits signals for other detectors to consume.
///
///     Configuration loaded from: useragent.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:UserAgentContributor:*
/// </summary>
public partial class UserAgentContributor : ConfiguredContributorBase
{

    private readonly ILogger<UserAgentContributor> _logger;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;

    public UserAgentContributor(
        ILogger<UserAgentContributor> logger,
        IOptions<BotDetectionOptions> options,
        IDetectorConfigProvider configProvider,
        ICompiledPatternCache? patternCache = null)
        : base(configProvider)
    {
        _logger = logger;
        _options = options.Value;
        _patternCache = patternCache;
    }

    public override string Name => "UserAgent";
    public override int Priority => Manifest?.Priority ?? 10;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters from YAML
    private int MinUaLength => GetParam("min_ua_length", 10);
    private double MissingUaConfidence => GetParam("missing_ua_confidence", 0.8);
    private double PatternMatchConfidence => GetParam("pattern_match_confidence", 0.9);
    private double SuspiciousConfidence => GetParam("suspicious_confidence", 0.6);
    private double ToolHeaderMatchConfidence => GetParam("tool_header_match_confidence", 0.7);
    private double ToolHeaderMismatchConfidence => GetParam("tool_header_mismatch_confidence", 0.5);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var userAgent = state.UserAgent;

        if (string.IsNullOrWhiteSpace(userAgent))
            return Task.FromResult(Single(BotContribution(
                "UserAgent",
                "Missing User-Agent header",
                confidenceOverride: MissingUaConfidence,
                botType: BotType.Unknown.ToString())));

        var contributions = new List<DetectionContribution>();

        // Check for known bot UA patterns (previously whitelisted for early exit).
        // Now emits a regular bot contribution — actual IP/DNS verification is
        // handled by VerifiedBotContributor. UA alone is trivially spoofable.
        // Extract browser family and version for all UAs (bots and humans alike)
        var (family, familyVersion) = ExtractBrowserFamily(userAgent);

        var (isWhitelisted, whitelistName) = CheckWhitelist(userAgent);
        if (isWhitelisted)
        {
            state.WriteSignals([
                new(SignalKeys.UserAgent, userAgent),
                new(SignalKeys.UserAgentIsBot, true),
                new(SignalKeys.UserAgentBotType, BotType.SearchEngine.ToString()),
                new(SignalKeys.UserAgentBotName, whitelistName!),
                new(SignalKeys.UserAgentFamily, whitelistName!),
                new(SignalKeys.UserAgentFamilyVersion, familyVersion ?? "")
            ]);
            return Task.FromResult(Single(BotContribution(
                    "UserAgent",
                    $"Known bot UA pattern: {whitelistName}",
                    confidenceOverride: PatternMatchConfidence,
                    botType: BotType.SearchEngine.ToString(),
                    botName: whitelistName)
                with
                {
                    Weight = WeightBotSignal
                }));
        }

        // Check for known bot patterns
        var (isBot, confidence, botType, botName, reason) = AnalyzeUserAgent(userAgent);

        if (isBot)
        {
            // For tool-type UAs, verify headers match the tool's expected fingerprint
            if (botType == BotType.Tool)
                confidence = VerifyToolHeaders(state, confidence, botName, out reason);

            state.WriteSignals([
                new(SignalKeys.UserAgent, userAgent),
                new(SignalKeys.UserAgentIsBot, true),
                new(SignalKeys.UserAgentBotType, botType?.ToString() ?? "Unknown"),
                new(SignalKeys.UserAgentBotName, botName ?? ""),
                new(SignalKeys.UserAgentFamily, botName ?? family),
                new(SignalKeys.UserAgentFamilyVersion, familyVersion ?? "")
            ]);
            contributions.Add(BotContribution(
                    "UserAgent",
                    reason,
                    confidenceOverride: confidence,
                    botType: botType?.ToString(),
                    botName: botName)
                with
                {
                    Weight = WeightBotSignal
                });
        }
        else
        {
            // Emit negative contribution (human-like) with signals for other detectors
            state.WriteSignals([
                new(SignalKeys.UserAgent, userAgent),
                new(SignalKeys.UserAgentIsBot, false),
                new(SignalKeys.UserAgentFamily, family),
                new(SignalKeys.UserAgentFamilyVersion, familyVersion ?? "")
            ]);
            contributions.Add(HumanContribution(
                    "UserAgent",
                    "User-Agent appears normal"));
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    /// Extract browser family and version from a user agent string.
    /// Returns a non-PII family name (e.g., "Chrome", "Firefox") that's safe for signals/dashboard.
    /// </summary>
    private static (string family, string? version) ExtractBrowserFamily(string ua)
    {
        // Order matters: check specific browsers before generic Chrome/Safari
        if (ua.Contains("Edg/", StringComparison.Ordinal))
            return ("Edge", ExtractVersion(ua, "Edg/"));
        if (ua.Contains("OPR/", StringComparison.Ordinal))
            return ("Opera", ExtractVersion(ua, "OPR/"));
        if (ua.Contains("Vivaldi/", StringComparison.Ordinal))
            return ("Vivaldi", ExtractVersion(ua, "Vivaldi/"));
        if (ua.Contains("Brave", StringComparison.Ordinal) && ua.Contains("Chrome/", StringComparison.Ordinal))
            return ("Brave", ExtractVersion(ua, "Chrome/"));
        if (ua.Contains("Firefox/", StringComparison.Ordinal))
            return ("Firefox", ExtractVersion(ua, "Firefox/"));
        if (ua.Contains("Chrome/", StringComparison.Ordinal))
            return ("Chrome", ExtractVersion(ua, "Chrome/"));
        if (ua.Contains("Safari/", StringComparison.Ordinal) && ua.Contains("Version/", StringComparison.Ordinal))
            return ("Safari", ExtractVersion(ua, "Version/"));
        if (ua.Contains("MSIE", StringComparison.Ordinal) || ua.Contains("Trident/", StringComparison.Ordinal))
            return ("Internet Explorer", null);
        // Bot/tool UAs
        if (ua.Contains("curl/", StringComparison.OrdinalIgnoreCase))
            return ("curl", ExtractVersion(ua, "curl/"));
        if (ua.Contains("python", StringComparison.OrdinalIgnoreCase))
            return ("Python", null);
        if (ua.Contains("Go-http-client", StringComparison.Ordinal))
            return ("Go", null);
        if (ua.Contains("Java/", StringComparison.Ordinal))
            return ("Java", ExtractVersion(ua, "Java/"));
        if (ua.Contains("node", StringComparison.OrdinalIgnoreCase))
            return ("Node.js", null);
        if (ua.Contains("wget", StringComparison.OrdinalIgnoreCase))
            return ("wget", null);

        return ("Other", null);
    }

    private static string? ExtractVersion(string ua, string token)
    {
        var idx = ua.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + token.Length;
        var end = start;
        while (end < ua.Length && ua[end] != ' ' && ua[end] != ';' && ua[end] != ')') end++;
        var ver = ua[start..end];
        // Return just major.minor for brevity
        var dotCount = 0;
        for (var i = 0; i < ver.Length; i++)
        {
            if (ver[i] == '.') dotCount++;
            if (dotCount == 2) return ver[..i];
        }
        return ver;
    }

    private (bool isWhitelisted, string? name) CheckWhitelist(string userAgent)
    {
        // First check YAML config for known bot patterns
        var knownBotPatterns = GetStringListParam("known_bot_patterns");
        foreach (var pattern in knownBotPatterns)
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (true, pattern);

        // Then check runtime options
        foreach (var pattern in _options.WhitelistedBotPatterns)
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return (true, pattern);

        return (false, null);
    }

    private (bool isBot, double confidence, BotType? type, string? name, string reason)
        AnalyzeUserAgent(string userAgent)
    {
        // Check common bot indicators FIRST - these provide specific names and types
        if (IsCommonBotPattern(userAgent, out var botType, out var botName))
            return (true, PatternMatchConfidence, botType, botName, $"Known bot pattern: {botName}");

        // Check compiled patterns from data sources (generic regex patterns)
        if (_patternCache != null)
            if (_patternCache.MatchesAnyPattern(userAgent, out var matchedValue))
            {
                // Try to extract a meaningful name from the UA string
                var extractedName = ExtractNameFromUserAgent(userAgent);
                return (true, PatternMatchConfidence, BotType.Unknown, extractedName,
                    extractedName != null
                        ? $"Known bot pattern: {extractedName}"
                        : DescribeMatchedUserAgent(userAgent, matchedValue));
            }

        // Check for suspicious patterns
        if (IsSuspiciousUserAgent(userAgent, out var suspiciousReason))
        {
            var extractedName2 = ExtractNameFromUserAgent(userAgent);
            return (true, SuspiciousConfidence, BotType.Unknown, extractedName2, suspiciousReason);
        }

        return (false, 0.0, null, null, "Normal user agent");
    }

    // Browser-only headers that real HTTP tools never send.
    // Used to verify tool UA claims — if these are present, the UA is likely spoofed.
    private static readonly string[] BrowserOnlyHeaders =
        ["Sec-Fetch-Mode", "Sec-Fetch-Site", "Sec-Fetch-Dest"];

    private static readonly (string pattern, BotType type, string name)[] CommonBotPatterns =
    [
        // Developer HTTP tools (libraries/CLIs — not automation frameworks)
        ("curl/", BotType.Tool, "curl"),
        ("wget/", BotType.Tool, "wget"),
        ("python-requests", BotType.Tool, "python-requests"),
        ("python-urllib", BotType.Tool, "python-urllib"),
        ("python-httpx", BotType.Tool, "python-httpx"),
        ("aiohttp", BotType.Tool, "aiohttp"),
        ("httpie", BotType.Tool, "HTTPie"),
        ("java/", BotType.Tool, "Java HTTP client"),
        ("apache-httpclient", BotType.Tool, "Apache HttpClient"),
        ("okhttp", BotType.Tool, "OkHttp"),
        ("go-http-client", BotType.Tool, "Go HTTP client"),
        ("node-fetch", BotType.Tool, "node-fetch"),
        ("axios/", BotType.Tool, "axios"),
        // Automation / scraping frameworks
        ("scrapy", BotType.Scraper, "Scrapy"),
        ("selenium", BotType.Scraper, "Selenium"),
        ("headless", BotType.Scraper, "Headless browser"),
        ("phantomjs", BotType.Scraper, "PhantomJS"),
        ("puppeteer", BotType.Scraper, "Puppeteer"),
        ("playwright", BotType.Scraper, "Playwright"),
        ("httrack", BotType.Scraper, "HTTrack"),
        ("libwww-perl", BotType.Scraper, "libwww-perl"),
        ("colly", BotType.Scraper, "Colly"),
        // Search engines (when not whitelisted)
        ("Googlebot", BotType.SearchEngine, "Googlebot"),
        ("bingbot", BotType.SearchEngine, "Bingbot"),
        ("YandexBot", BotType.SearchEngine, "YandexBot"),
        ("Baiduspider", BotType.SearchEngine, "Baiduspider"),
        ("DuckDuckBot", BotType.SearchEngine, "DuckDuckBot"),
        // Social media bots
        ("facebookexternalhit", BotType.SocialMediaBot, "FacebookBot"),
        ("Facebot", BotType.SocialMediaBot, "FacebookBot"),
        ("Twitterbot", BotType.SocialMediaBot, "TwitterBot"),
        ("LinkedInBot", BotType.SocialMediaBot, "LinkedInBot"),
        ("Slackbot", BotType.SocialMediaBot, "SlackBot"),
        ("Discordbot", BotType.SocialMediaBot, "DiscordBot"),
        ("TelegramBot", BotType.SocialMediaBot, "TelegramBot"),
        ("WhatsApp", BotType.SocialMediaBot, "WhatsApp"),
        // AI / LLM crawlers
        ("GPTBot", BotType.AiBot, "GPTBot"),
        ("ChatGPT-User", BotType.AiBot, "ChatGPT"),
        ("ClaudeBot", BotType.AiBot, "ClaudeBot"),
        ("Claude-Web", BotType.AiBot, "Claude"),
        ("anthropic-ai", BotType.AiBot, "Anthropic"),
        ("CCBot", BotType.AiBot, "CommonCrawl"),
        ("cohere-ai", BotType.AiBot, "Cohere"),
        ("PerplexityBot", BotType.AiBot, "PerplexityBot"),
        ("Bytespider", BotType.AiBot, "Bytespider"),
        // Monitoring / uptime bots
        ("UptimeRobot", BotType.MonitoringBot, "UptimeRobot"),
        ("Pingdom", BotType.MonitoringBot, "Pingdom"),
        ("StatusCake", BotType.MonitoringBot, "StatusCake"),
        ("Site24x7", BotType.MonitoringBot, "Site24x7"),
        ("NewRelicPinger", BotType.MonitoringBot, "New Relic"),
        ("Datadog", BotType.MonitoringBot, "Datadog"),
    ];

    private static bool IsCommonBotPattern(string userAgent, out BotType? botType, out string? botName)
    {
        foreach (var (pattern, type, name) in CommonBotPatterns)
            if (userAgent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                botType = type;
                botName = name;
                return true;
            }

        botType = null;
        botName = null;
        return false;
    }

    private bool IsSuspiciousUserAgent(string userAgent, out string reason)
    {
        // Very short user agent - threshold from YAML
        if (userAgent.Length < MinUaLength)
        {
            reason = $"Suspiciously short User-Agent (< {MinUaLength} chars)";
            return true;
        }

        // Contains "bot" or "crawler" but not whitelisted
        if (BotKeywordRegex().IsMatch(userAgent))
        {
            reason = "Contains bot/crawler keyword";
            return true;
        }

        // Empty version numbers (common in simple bots)
        if (BareMozillaRegex().IsMatch(userAgent))
        {
            reason = "Bare Mozilla version without details";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    ///     Extracts a human-readable name from a User-Agent string.
    ///     Handles formats like "ToolName/1.2.3", "Mozilla/5.0 (compatible; BotName/2.0; ...)", etc.
    /// </summary>
    private static string? ExtractNameFromUserAgent(string userAgent)
    {
        // Try "compatible; BotName/version" format (e.g., bingbot, YandexBot)
        var compatibleMatch = CompatibleBotRegex().Match(userAgent);
        if (compatibleMatch.Success)
            return compatibleMatch.Groups[1].Value;

        // Try simple "ToolName/version" format (e.g., curl/8.4.0, Scrapy/2.11)
        // Only use this for short UAs (not full browser strings)
        if (!userAgent.Contains("Mozilla/") || userAgent.Length < 60)
        {
            var simpleMatch = SimpleToolRegex().Match(userAgent);
            if (simpleMatch.Success)
            {
                var name = simpleMatch.Groups[1].Value;
                // Skip generic names
                if (!string.Equals(name, "Mozilla", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(name, "HTTP", StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }

        return null;
    }

    /// <summary>
    ///     Generates a human-readable description of why a User-Agent matched a bot pattern.
    ///     Never exposes raw regex patterns to the user.
    /// </summary>
    private static string DescribeMatchedUserAgent(string userAgent, string? matchedValue)
    {
        // Simple tool-style UA: "curl/7.64.1", "wget/1.21", "python-requests/2.28.0"
        if (SimpleToolRegex().IsMatch(userAgent) && !userAgent.Contains(' '))
            return $"Simple tool-style User-Agent: {Truncate(userAgent, 60)} (no browser information)";

        // UA contains bot/crawler/spider keywords
        if (BotKeywordRegex().IsMatch(userAgent))
            return $"User-Agent contains bot identifier: {Truncate(userAgent, 60)}";

        // Very short or minimal UA
        if (userAgent.Length < 30)
            return $"Minimal User-Agent: {Truncate(userAgent, 60)} (too short for a real browser)";

        // Matched value is meaningful - describe what was found
        if (!string.IsNullOrEmpty(matchedValue) && matchedValue.Length > 2)
            return $"User-Agent matches known bot signature: {Truncate(userAgent, 60)}";

        // Generic fallback - still human-readable
        return $"User-Agent matches known bot pattern database: {Truncate(userAgent, 60)}";
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    /// <summary>
    ///     Verifies that a tool UA claim is consistent with the tool's expected header fingerprint.
    ///     Real HTTP tools (curl, wget, etc.) don't send browser-specific headers like Sec-Fetch-*.
    ///     If browser-only headers are present, the UA is likely spoofed → higher confidence.
    /// </summary>
    private double VerifyToolHeaders(BlackboardState state, double baseConfidence, string? toolName, out string reason)
    {
        var headers = state.HttpContext.Request.Headers;

        // Count browser-only headers present — real tools send 0 of these
        var browserHeaderCount = 0;
        foreach (var header in BrowserOnlyHeaders)
            if (headers.ContainsKey(header))
                browserHeaderCount++;

        // Also check Accept-Language with locale (e.g., "en-US,en;q=0.9") — tools don't send this
        var acceptLang = headers["Accept-Language"].FirstOrDefault();
        if (!string.IsNullOrEmpty(acceptLang) && acceptLang.Contains(','))
            browserHeaderCount++;

        if (browserHeaderCount > 0)
        {
            // Browser headers present with tool UA → likely spoofed
            reason = $"Tool UA ({toolName}) with {browserHeaderCount} browser-only header(s) — likely spoofed";
            return ToolHeaderMismatchConfidence + (browserHeaderCount * 0.05);
        }

        // Headers consistent with tool fingerprint
        reason = $"Confirmed tool: {toolName} (headers match expected fingerprint)";
        return ToolHeaderMatchConfidence;
    }

    [GeneratedRegex(@"\b(bot|crawler|spider|scraper)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BotKeywordRegex();

    [GeneratedRegex(@"Mozilla/\d+\.\d+\s*$")]
    private static partial Regex BareMozillaRegex();

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeVersionRegex();

    [GeneratedRegex(@"compatible;\s*([A-Za-z][A-Za-z0-9_-]+)")]
    private static partial Regex CompatibleBotRegex();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_.-]+)/[\d.]")]
    private static partial Regex SimpleToolRegex();
}

/// <summary>
///     Inconsistency detection that runs after raw signals are collected.
///     Looks for mismatches between claimed identity and actual behavior.
///
///     Configuration loaded from: inconsistency.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:InconsistencyContributor:*
/// </summary>
public partial class InconsistencyContributor : ConfiguredContributorBase
{

    private readonly ILogger<InconsistencyContributor> _logger;

    public InconsistencyContributor(
        ILogger<InconsistencyContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "Inconsistency";
    public override int Priority => Manifest?.Priority ?? 50;

    // Wait for UA and IP signals
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.WhenSignalExists(SignalKeys.UserAgent)
    ];

    // Config-driven parameters from YAML
    private double DatacenterBrowserConfidence => GetParam("datacenter_browser_confidence", 0.7);
    private double MissingLanguageConfidence => GetParam("missing_language_confidence", 0.5);
    private double MissingClientHintsConfidence => GetParam("missing_client_hints_confidence", 0.2);
    private double OutdatedBrowserConfidence => GetParam("outdated_browser_confidence", 0.3);
    private int MinChromeVersion => GetParam("min_chrome_version", 90);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        var userAgent = state.GetSignal<string>(SignalKeys.UserAgent) ?? "";
        var isDatacenter = state.GetSignal<bool>(SignalKeys.IpIsDatacenter);
        var headers = state.HttpContext.Request.Headers;

        // Check for datacenter IP + browser UA (common bot pattern)
        if (isDatacenter && LooksLikeBrowser(userAgent))
            contributions.Add(BotContribution(
                "Inconsistency",
                "Browser User-Agent from datacenter IP",
                confidenceOverride: DatacenterBrowserConfidence,
                weightMultiplier: 1.5,
                botType: BotType.Unknown.ToString()));

        // WebSocket upgrade requests (RFC 6455) legitimately omit Accept-Language
        // and Client Hints — browsers don't send these on WS upgrades.
        // Read the signal written by HeaderContributor (Wave 0, Priority 10) rather
        // than re-parsing the Upgrade header — single source of truth.
        var isWebSocketUpgrade = state.GetSignal<bool>("header.is_websocket_upgrade");

        // Same-origin fetch (Sec-Fetch-Site: same-origin) legitimately omits Accept-Language
        // and Client Hints — browser fetch() API doesn't always include them.
        var isSameOriginFetch = state.GetSignal<bool>(SignalKeys.HeaderSecFetchSameOrigin);

        // Check for missing Accept-Language with browser UA
        // Skip for WebSocket upgrades and same-origin fetch which don't carry Accept-Language
        if (LooksLikeBrowser(userAgent) && !headers.ContainsKey("Accept-Language")
            && !isWebSocketUpgrade && !isSameOriginFetch)
            contributions.Add(BotContribution(
                "Inconsistency",
                "Browser User-Agent without Accept-Language header",
                confidenceOverride: MissingLanguageConfidence,
                botType: BotType.Unknown.ToString()));

        // Check for Chrome UA without sec-ch-ua headers
        // Note: Service worker, fetch API, WebSocket upgrades, same-origin fetch,
        // and some browser configurations may not send Client Hints.
        var path = state.HttpContext.Request.Path.Value?.ToLowerInvariant() ?? "";
        var isLegitimateNoHintsRequest = isWebSocketUpgrade || isSameOriginFetch ||
                                         path.Contains("serviceworker") ||
                                         path.Contains("sw.js") ||
                                         path.Contains("worker");

        if (userAgent.Contains("Chrome/") && !headers.ContainsKey("sec-ch-ua") && !isLegitimateNoHintsRequest)
            contributions.Add(BotContribution(
                "Inconsistency",
                "Chrome User-Agent without Client Hints",
                confidenceOverride: MissingClientHintsConfidence,
                botType: BotType.Scraper.ToString()));

        // Check for modern browser claiming old version
        if (IsOutdatedBrowser(userAgent))
            contributions.Add(BotContribution(
                "Inconsistency",
                "Outdated browser version in User-Agent",
                confidenceOverride: OutdatedBrowserConfidence,
                botType: BotType.Unknown.ToString()));

        if (contributions.Count == 0)
            // No inconsistencies found - add negative signal (human indicator)
            contributions.Add(HumanContribution(
                "Inconsistency",
                "No header/UA inconsistencies detected"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private static bool LooksLikeBrowser(string userAgent)
    {
        return userAgent.Contains("Mozilla/") &&
               (userAgent.Contains("Chrome") || userAgent.Contains("Firefox") ||
                userAgent.Contains("Safari") || userAgent.Contains("Edge"));
    }

    private bool IsOutdatedBrowser(string userAgent)
    {
        // Check for very old Chrome versions - threshold from YAML
        var chromeMatch = InconsistencyChromeVersionRegex().Match(userAgent);
        if (chromeMatch.Success && int.TryParse(chromeMatch.Groups[1].Value, out var version))
            return version < MinChromeVersion;

        return false;
    }

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex InconsistencyChromeVersionRegex();
}

/// <summary>
///     Expensive AI-based detector that only runs when risk is elevated.
///     Uses trigger conditions to avoid running on obvious humans.
///
///     Configuration loaded from: ai.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:AiContributor:*
/// </summary>
public class AiContributor : ConfiguredContributorBase
{
    private readonly ILogger<AiContributor> _logger;

    public AiContributor(
        ILogger<AiContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "AI";
    public override int Priority => Manifest?.Priority ?? 100;
    public override TimeSpan ExecutionTimeout => TimeSpan.FromMilliseconds(Config.Timing.TimeoutMs > 0 ? Config.Timing.TimeoutMs : 5000);

    // Config-driven parameters from YAML
    private double HighRiskThreshold => GetParam("high_risk_threshold", 0.8);
    private double MediumRiskThreshold => GetParam("medium_risk_threshold", 0.5);
    private double HighRiskAdjustment => GetParam("high_risk_adjustment", 0.2);
    private int MinDetectorCount => GetParam("min_detector_count", 2);

    // Only run when risk is medium or higher AND we have signals to analyze
    public override IReadOnlyList<TriggerCondition> TriggerConditions =>
    [
        Triggers.AllOf(
            Triggers.WhenRiskMediumOrHigher,
            Triggers.WhenDetectorCount(MinDetectorCount)
        )
    ];

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("AI detector running for request {RequestId}", state.RequestId);

        // In a real implementation, this would call ONNX or LLM
        // For now, return a placeholder that demonstrates the pattern
        await Task.Delay(10, cancellationToken); // Simulate some processing

        // Example: AI confirms or adjusts the existing risk assessment
        var currentRisk = state.CurrentRiskScore;

        if (currentRisk > HighRiskThreshold)
            return Single(BotContribution(
                "AI",
                "AI analysis confirms high-risk signals",
                confidenceOverride: HighRiskAdjustment,
                weightMultiplier: 0.5));

        if (currentRisk > MediumRiskThreshold)
            // Uncertain - AI provides additional signal
            return Single(DetectionContribution.Info(
                Name, "AI",
                "AI analysis: borderline case, monitoring"));

        return None();
    }
}