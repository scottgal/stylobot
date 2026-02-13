using System.Collections.Immutable;
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

        // Check for known good bots (whitelisted)
        var (isWhitelisted, whitelistName) = CheckWhitelist(userAgent);
        if (isWhitelisted)
            return Task.FromResult(Single(DetectionContribution.VerifiedGoodBot(
                    Name,
                    $"Whitelisted bot pattern: {whitelistName}",
                    whitelistName!)
                with
                {
                    Weight = WeightVerified,
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.UserAgent, userAgent)
                        .Add(SignalKeys.UserAgentIsBot, true)
                        .Add(SignalKeys.UserAgentBotType, BotType.SearchEngine.ToString())
                        .Add(SignalKeys.UserAgentBotName, whitelistName!)
                }));

        // Check for known bot patterns
        var (isBot, confidence, botType, botName, reason) = AnalyzeUserAgent(userAgent);

        if (isBot)
            contributions.Add(BotContribution(
                    "UserAgent",
                    reason,
                    confidenceOverride: confidence,
                    botType: botType?.ToString(),
                    botName: botName)
                with
                {
                    Weight = WeightBotSignal,
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.UserAgent, userAgent)
                        .Add(SignalKeys.UserAgentIsBot, true)
                        .Add(SignalKeys.UserAgentBotType, botType?.ToString() ?? "Unknown")
                        .Add(SignalKeys.UserAgentBotName, botName ?? "")
                });
        else
            // Emit negative contribution (human-like) with signals for other detectors
            contributions.Add(HumanContribution(
                    "UserAgent",
                    "User-Agent appears normal")
                with
                {
                    Signals = ImmutableDictionary<string, object>.Empty
                        .Add(SignalKeys.UserAgent, userAgent)
                        .Add(SignalKeys.UserAgentIsBot, false)
                });

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
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
        // Check compiled patterns from data sources
        if (_patternCache != null)
            if (_patternCache.MatchesAnyPattern(userAgent, out var matchedPattern))
                return (true, PatternMatchConfidence, BotType.Unknown, null,
                    $"Matched pattern: {matchedPattern}");

        // Check common bot indicators
        if (IsCommonBotPattern(userAgent, out var botType, out var botName))
            return (true, PatternMatchConfidence, botType, botName, $"Known bot pattern: {botName}");

        // Check for suspicious patterns
        if (IsSuspiciousUserAgent(userAgent, out var suspiciousReason))
            return (true, SuspiciousConfidence, BotType.Unknown, null, suspiciousReason);

        return (false, 0.0, null, null, "Normal user agent");
    }

    private static bool IsCommonBotPattern(string userAgent, out BotType? botType, out string? botName)
    {
        // Common bot patterns with high confidence
        // Using Scraper for automation tools since BotType.Automation doesn't exist
        var patterns = new (string pattern, BotType type, string name)[]
        {
            ("curl/", BotType.Scraper, "curl"),
            ("wget/", BotType.Scraper, "wget"),
            ("python-requests", BotType.Scraper, "python-requests"),
            ("python-urllib", BotType.Scraper, "python-urllib"),
            ("scrapy", BotType.Scraper, "Scrapy"),
            ("selenium", BotType.Scraper, "Selenium"),
            ("headless", BotType.Scraper, "Headless browser"),
            ("phantomjs", BotType.Scraper, "PhantomJS"),
            ("puppeteer", BotType.Scraper, "Puppeteer"),
            ("playwright", BotType.Scraper, "Playwright"),
            ("httrack", BotType.Scraper, "HTTrack"),
            ("libwww-perl", BotType.Scraper, "libwww-perl"),
            ("java/", BotType.Scraper, "Java HTTP client"),
            ("apache-httpclient", BotType.Scraper, "Apache HttpClient"),
            ("okhttp", BotType.Scraper, "OkHttp"),
            ("go-http-client", BotType.Scraper, "Go HTTP client"),
            ("node-fetch", BotType.Scraper, "node-fetch"),
            ("axios/", BotType.Scraper, "axios")
        };

        foreach (var (pattern, type, name) in patterns)
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

    [GeneratedRegex(@"\b(bot|crawler|spider|scraper)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BotKeywordRegex();

    [GeneratedRegex(@"Mozilla/\d+\.\d+\s*$")]
    private static partial Regex BareMozillaRegex();

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeVersionRegex();
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

        // Check for missing Accept-Language with browser UA
        if (LooksLikeBrowser(userAgent) && !headers.ContainsKey("Accept-Language"))
            contributions.Add(BotContribution(
                "Inconsistency",
                "Browser User-Agent without Accept-Language header",
                confidenceOverride: MissingLanguageConfidence,
                botType: BotType.Unknown.ToString()));

        // Check for Chrome UA without sec-ch-ua headers
        // Note: Service worker, fetch API, and some browser configurations may not send Client Hints
        // Only flag if path doesn't suggest legitimate browser request
        var path = state.HttpContext.Request.Path.Value?.ToLowerInvariant() ?? "";
        var isLegitimateNoHintsRequest = path.Contains("serviceworker") ||
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