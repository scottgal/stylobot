using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Multi-layer identity correlation contributor.
///     Analyzes consistency across multiple detection layers to identify sophisticated bots.
///     Best-in-breed approach:
///     - Cross-layer consistency analysis (TLS + TCP + HTTP/2 + Headers)
///     - OS fingerprint correlation (TCP TTL vs User-Agent claimed OS)
///     - Browser fingerprint correlation (TLS + HTTP/2 vs User-Agent claimed browser)
///     - Geographic correlation (IP geolocation vs language headers)
///     - Temporal correlation (timing patterns across layers)
///     This runs in a later wave after all fingerprinting contributors have completed.
///     Raises signals for final waveform analysis:
///     - correlation.os_mismatch
///     - correlation.browser_mismatch
///     - correlation.geo_mismatch
///     - correlation.consistency_score
///     - correlation.anomaly_layers
/// </summary>
public class MultiLayerCorrelationContributor : ContributingDetectorBase
{
    private readonly ILogger<MultiLayerCorrelationContributor> _logger;

    public MultiLayerCorrelationContributor(ILogger<MultiLayerCorrelationContributor> logger)
    {
        _logger = logger;
    }

    public override string Name => "MultiLayerCorrelation";
    public override int Priority => 4; // Run late, after fingerprinting

    // Requires UA signal plus at least one fingerprint layer.
    // In environments without a reverse proxy that injects TLS/TCP/HTTP2 headers,
    // not all fingerprint signals will be available, so we use AnyOfTrigger.
    public override IReadOnlyList<TriggerCondition> TriggerConditions => new TriggerCondition[]
    {
        new AllOfTrigger(new TriggerCondition[]
        {
            new SignalExistsTrigger(SignalKeys.UserAgent),
            new AnyOfTrigger(new TriggerCondition[]
            {
                new SignalExistsTrigger(SignalKeys.TcpOsHint),
                new SignalExistsTrigger(SignalKeys.TlsProtocol),
                new SignalExistsTrigger(SignalKeys.H2Protocol),
                new SignalExistsTrigger(SignalKeys.H3Protocol)
            })
        })
    };

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();

        try
        {
            var anomalyCount = 0;
            var anomalyLayers = new List<string>();

            // Extract signals from previous detectors
            var tcpOsHint = GetSignal<string>(state, SignalKeys.TcpOsHintTtl);
            var tcpWindowOsHint = GetSignal<string>(state, SignalKeys.TcpOsHintWindow);
            var userAgentOs = GetSignal<string>(state, SignalKeys.UserAgentOs);
            var userAgentBrowser = GetSignal<string>(state, SignalKeys.UserAgentBrowser);
            var h2ClientType = GetSignal<string>(state, SignalKeys.H2ClientType)
                               ?? GetSignal<string>(state, SignalKeys.H3ClientType);
            var tlsProtocol = GetSignal<string>(state, SignalKeys.TlsProtocol);
            var ipIsDatacenter = GetSignal<bool>(state, SignalKeys.IpIsDatacenter);

            // 1. OS Fingerprint Correlation
            var osMismatch = AnalyzeOsCorrelation(state, tcpOsHint, tcpWindowOsHint, userAgentOs);
            if (osMismatch)
            {
                anomalyCount++;
                anomalyLayers.Add("OS");

                contributions.Add(DetectionContribution.Bot(
                    Name, "Correlation", 0.65,
                    $"OS mismatch detected: TCP indicates {tcpOsHint ?? tcpWindowOsHint}, UA claims {userAgentOs}",
                    weight: 1.7,
                    botType: BotType.Scraper.ToString()));
            }

            // 2. Browser Fingerprint Correlation
            var browserMismatch = AnalyzeBrowserCorrelation(state, h2ClientType, userAgentBrowser, tlsProtocol);
            if (browserMismatch)
            {
                anomalyCount++;
                anomalyLayers.Add("Browser");

                contributions.Add(DetectionContribution.Bot(
                    Name, "Correlation", 0.7,
                    $"Browser mismatch: HTTP/2 indicates {h2ClientType}, UA claims {userAgentBrowser}",
                    weight: 1.8,
                    botType: BotType.Scraper.ToString()));
            }

            // 3. TLS vs User-Agent Correlation
            var tlsMismatch = AnalyzeTlsCorrelation(state, tlsProtocol, userAgentBrowser);
            if (tlsMismatch)
            {
                anomalyCount++;
                anomalyLayers.Add("TLS");

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Correlation",
                    ConfidenceDelta = 0.4,
                    Weight = 1.4,
                    Reason = $"Encryption version does not match what {userAgentBrowser} would normally use"
                });
            }

            // 4. Geographic Correlation
            var geoMismatch = AnalyzeGeoCorrelation(state);
            if (geoMismatch)
            {
                anomalyCount++;
                anomalyLayers.Add("Geographic");

                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Correlation",
                    ConfidenceDelta = 0.3,
                    Weight = 1.2,
                    Reason = "IP address location does not match the language preference claimed by the browser"
                });
            }

            // 5. Datacenter + Browser Claims = Suspicious
            if (ipIsDatacenter && !string.IsNullOrEmpty(userAgentBrowser) &&
                (userAgentBrowser.Contains("Chrome") || userAgentBrowser.Contains("Firefox") ||
                 userAgentBrowser.Contains("Safari")))
            {
                anomalyCount++;
                anomalyLayers.Add("IP-Browser");

                contributions.Add(DetectionContribution.Bot(
                    Name, "Correlation", 0.75,
                    $"Datacenter IP with browser User-Agent: {userAgentBrowser}",
                    weight: 1.9,
                    botType: BotType.MaliciousBot.ToString()));
            }

            // 6. Calculate overall consistency score
            var totalLayers = 5; // OS, Browser, TLS, Geo, IP-Browser
            var consistencyScore = 1.0 - (double)anomalyCount / totalLayers;
            state.WriteSignals([
                new(SignalKeys.CorrelationConsistencyScore, consistencyScore),
                new(SignalKeys.CorrelationAnomalyCount, anomalyCount),
                new("correlation.anomaly_layers", string.Join(",", anomalyLayers))
            ]);

            // High anomaly count = very suspicious
            if (anomalyCount >= 3)
                contributions.Add(DetectionContribution.Bot(
                    Name, "Correlation", 0.85,
                    $"Multiple layer mismatches detected ({anomalyCount}/{totalLayers}): {string.Join(", ", anomalyLayers)}",
                    weight: 2.0,
                    botType: BotType.MaliciousBot.ToString()));
            else if (anomalyCount >= 2)
                contributions.Add(DetectionContribution.Bot(
                    Name, "Correlation", 0.6,
                    $"Cross-layer inconsistencies: {string.Join(", ", anomalyLayers)}",
                    weight: 1.5,
                    botType: BotType.Scraper.ToString()));

            // 7. Perfect consistency across all layers = likely human
            if (anomalyCount == 0 && !string.IsNullOrEmpty(tcpOsHint) && !string.IsNullOrEmpty(userAgentOs))
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Correlation",
                    ConfidenceDelta = -0.25,
                    Weight = 1.8,
                    Reason = "All signals consistent: operating system, browser, encryption, and location all match"
                });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in multi-layer correlation analysis");
            state.WriteSignal("correlation.analysis_error", ex.Message);
        }

        // Always add at least one contribution
        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Correlation",
                ConfidenceDelta = 0.0,
                Weight = 1.0,
                Reason = "Cross-signal consistency check complete (not enough data to compare)"
            });
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    private bool AnalyzeOsCorrelation(BlackboardState state, string? tcpOsHint, string? tcpWindowOsHint, string? userAgentOs)
    {
        if (string.IsNullOrEmpty(tcpOsHint) && string.IsNullOrEmpty(tcpWindowOsHint)) return false;
        if (string.IsNullOrEmpty(userAgentOs)) return false;

        var networkOs = tcpOsHint ?? tcpWindowOsHint;
        state.WriteSignals([
            new("correlation.network_os", networkOs!),
            new("correlation.claimed_os", userAgentOs)
        ]);

        // Normalize OS names for comparison
        var networkOsNorm = NormalizeOsName(networkOs);
        var userAgentOsNorm = NormalizeOsName(userAgentOs);

        var mismatch = !networkOsNorm.Equals(userAgentOsNorm, StringComparison.OrdinalIgnoreCase) &&
                       !networkOsNorm.Contains(userAgentOsNorm, StringComparison.OrdinalIgnoreCase) &&
                       !userAgentOsNorm.Contains(networkOsNorm, StringComparison.OrdinalIgnoreCase);

        state.WriteSignal(SignalKeys.CorrelationOsMismatch, mismatch);
        return mismatch;
    }

    private bool AnalyzeBrowserCorrelation(BlackboardState state, string? h2ClientType, string? userAgentBrowser, string? tlsProtocol)
    {
        if (string.IsNullOrEmpty(h2ClientType) || string.IsNullOrEmpty(userAgentBrowser)) return false;

        state.WriteSignals([
            new("correlation.h2_client", h2ClientType),
            new("correlation.claimed_browser", userAgentBrowser)
        ]);

        // Check if HTTP/2 fingerprint matches claimed browser
        var browserNorm = NormalizeBrowserName(userAgentBrowser);
        var h2Norm = NormalizeBrowserName(h2ClientType);

        var mismatch = !h2Norm.Equals(browserNorm, StringComparison.OrdinalIgnoreCase) &&
                       !h2Norm.Contains(browserNorm, StringComparison.OrdinalIgnoreCase) &&
                       !h2ClientType.Contains("Bot") && // If H2 detected bot, that's already flagged
                       !string.IsNullOrEmpty(browserNorm);

        state.WriteSignal(SignalKeys.CorrelationBrowserMismatch, mismatch);
        return mismatch;
    }

    private bool AnalyzeTlsCorrelation(BlackboardState state, string? tlsProtocol, string? userAgentBrowser)
    {
        if (string.IsNullOrEmpty(tlsProtocol) || string.IsNullOrEmpty(userAgentBrowser)) return false;

        // Modern browsers (Chrome 80+, Firefox 75+, Safari 13+) should use TLS 1.2+
        var isModernBrowser = userAgentBrowser.Contains("Chrome") ||
                              userAgentBrowser.Contains("Firefox") ||
                              userAgentBrowser.Contains("Safari") ||
                              userAgentBrowser.Contains("Edge");

        var isOldTls = tlsProtocol.Contains("Tls") && !tlsProtocol.Contains("Tls12") && !tlsProtocol.Contains("Tls13");

        var mismatch = isModernBrowser && isOldTls;
        state.WriteSignal("correlation.tls_browser_mismatch", mismatch);

        return mismatch;
    }

    private bool AnalyzeGeoCorrelation(BlackboardState state)
    {
        // Check if IP geolocation matches Accept-Language headers
        var ipCountry = GetSignal<string>(state, "geo.country_code");
        var acceptLanguage = state.HttpContext.Request.Headers.AcceptLanguage.ToString();

        if (string.IsNullOrEmpty(ipCountry) || string.IsNullOrEmpty(acceptLanguage)) return false;

        state.WriteSignals([
            new("correlation.ip_country", ipCountry),
            new("correlation.accept_language", acceptLanguage)
        ]);

        // Extract primary language from Accept-Language (e.g., "en-US,en;q=0.9" -> "en-US")
        var primaryLang = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        state.WriteSignal("correlation.primary_language", primaryLang);

        // Very basic geo-language correlation (extend with proper mapping in production)
        var languageCountryMap = new Dictionary<string, string[]>
        {
            { "US", new[] { "en-US", "en", "es-US", "es" } },
            { "GB", new[] { "en-GB", "en" } },
            { "DE", new[] { "de-DE", "de" } },
            { "FR", new[] { "fr-FR", "fr" } },
            { "JP", new[] { "ja-JP", "ja" } },
            { "CN", new[] { "zh-CN", "zh" } },
            { "RU", new[] { "ru-RU", "ru" } }
        };

        if (languageCountryMap.TryGetValue(ipCountry, out var expectedLanguages))
        {
            var mismatch =
                !expectedLanguages.Any(lang => primaryLang.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
            state.WriteSignal("correlation.geo_mismatch", mismatch);
            return mismatch;
        }

        return false; // No data to correlate
    }

    private static string NormalizeOsName(string? os)
    {
        if (string.IsNullOrEmpty(os)) return string.Empty;

        os = os.ToLowerInvariant();
        // Order matters: Android UA contains "Linux", iOS contains "Mac"
        if (os.Contains("android")) return "android";
        if (os.Contains("ios") || os.Contains("iphone")) return "ios";
        if (os.Contains("windows")) return "windows";
        if (os.Contains("linux")) return "linux";
        if (os.Contains("mac") || os.Contains("darwin")) return "macos";
        if (os.Contains("unix") || os.Contains("bsd")) return "unix";

        return os;
    }

    private static string NormalizeBrowserName(string? browser)
    {
        if (string.IsNullOrEmpty(browser)) return string.Empty;

        browser = browser.ToLowerInvariant();
        // Order matters: Edge UA contains "Chrome", so check Edge/Opera first
        if (browser.Contains("edg")) return "edge";
        if (browser.Contains("opera") || browser.Contains("opr")) return "opera";
        if (browser.Contains("chrome")) return "chrome";
        if (browser.Contains("firefox")) return "firefox";
        if (browser.Contains("safari")) return "safari";

        return browser;
    }

    private static T? GetSignal<T>(BlackboardState state, string signalName)
    {
        if (state.Signals.TryGetValue(signalName, out var value) && value is T typedValue) return typedValue;
        return default;
    }
}