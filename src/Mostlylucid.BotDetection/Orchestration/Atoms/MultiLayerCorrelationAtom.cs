using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     ConstrainerAtom (per Taxonomy.md) that runs cross-layer consistency
///     analysis: OS fingerprint (TCP vs UA), browser fingerprint (HTTP/2 vs
///     UA), TLS vs UA, IP geolocation vs Accept-Language, and datacenter +
///     browser-UA combination.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>MultiLayerCorrelationContributor</c>. Priority 4 with the
///         trigger evaluated inline: needs
///         <see cref="SignalKeys.UserAgent"/> + at least one of
///         <see cref="SignalKeys.TcpOsHint"/>,
///         <see cref="SignalKeys.TlsProtocol"/>,
///         <see cref="SignalKeys.H2Protocol"/>,
///         <see cref="SignalKeys.H3Protocol"/>.
///     </para>
///     <para>
///         Pure signal reader -- no HttpContext needed for the core
///         analysis. Accept-Language is read via HttpContext for the geo
///         correlation arm since there is no upstream atom for it.
///     </para>
/// </remarks>
public sealed class MultiLayerCorrelationAtom : DetectorAtomBase
{
    private static readonly Dictionary<string, string[]> LanguageCountryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "US", ["en-US", "en", "es-US", "es"] },
        { "GB", ["en-GB", "en"] },
        { "DE", ["de-DE", "de"] },
        { "FR", ["fr-FR", "fr"] },
        { "JP", ["ja-JP", "ja"] },
        { "CN", ["zh-CN", "zh"] },
        { "RU", ["ru-RU", "ru"] }
    };

    private readonly ILogger<MultiLayerCorrelationAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public MultiLayerCorrelationAtom(
        ILogger<MultiLayerCorrelationAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: "MultiLayerCorrelation", category: "Correlation")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 4;

    // AllOf(UA, AnyOf(TCP|TLS|H2|H3)) can't be encoded in RequiredSignals -- we
    // require UA and evaluate the AnyOf arm inline.
    public override IReadOnlyList<string> RequiredSignals => new[] { SignalKeys.UserAgent };

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var hasFingerprintLayer = sink.ReadHint(SignalKeys.TcpOsHint) is not null
                                  || sink.ReadHint(SignalKeys.TlsProtocol) is not null
                                  || sink.ReadHint(SignalKeys.H2Protocol) is not null
                                  || sink.ReadHint(SignalKeys.H3Protocol) is not null;
        if (!hasFingerprintLayer) return Task.FromResult(None());

        sink.Raise("correlation.ran", sessionId);

        var contributions = new List<DetectionContribution>();
        var anomalyCount = 0;
        var anomalyLayers = new List<string>();

        try
        {
            var tcpOsHint = sink.ReadHint(SignalKeys.TcpOsHintTtl);
            var tcpWindowOsHint = sink.ReadHint(SignalKeys.TcpOsHintWindow);
            var userAgentOs = sink.ReadHint(SignalKeys.UserAgentOs);
            var userAgentBrowser = sink.ReadHint(SignalKeys.UserAgentBrowser);
            var h2ClientType = sink.ReadHint(SignalKeys.H2ClientType)
                               ?? sink.ReadHint(SignalKeys.H3ClientType);
            var tlsProtocol = sink.ReadHint(SignalKeys.TlsProtocol);
            var ipIsDatacenter = sink.ReadBoolHint(SignalKeys.IpIsDatacenter);

            if (AnalyzeOsCorrelation(sink, sessionId, tcpOsHint, tcpWindowOsHint, userAgentOs))
            {
                anomalyCount++;
                anomalyLayers.Add("OS");
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "os_mismatch_confidence", 0.65),
                    Weight = _configProvider.GetParameter(Name, "os_mismatch_weight", 1.7),
                    Reason = $"OS mismatch detected: TCP indicates {tcpOsHint ?? tcpWindowOsHint}, UA claims {userAgentOs}",
                    BotType = BotType.Scraper.ToString()
                });
            }

            if (AnalyzeBrowserCorrelation(sink, sessionId, h2ClientType, userAgentBrowser))
            {
                anomalyCount++;
                anomalyLayers.Add("Browser");
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "browser_mismatch_confidence", 0.7),
                    Weight = _configProvider.GetParameter(Name, "browser_mismatch_weight", 1.8),
                    Reason = $"Browser mismatch: HTTP/2 indicates {h2ClientType}, UA claims {userAgentBrowser}",
                    BotType = BotType.Scraper.ToString()
                });
            }

            if (AnalyzeTlsCorrelation(sink, sessionId, tlsProtocol, userAgentBrowser))
            {
                anomalyCount++;
                anomalyLayers.Add("TLS");
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "tls_mismatch_confidence", 0.4),
                    Weight = _configProvider.GetParameter(Name, "tls_mismatch_weight", 1.4),
                    Reason = $"Encryption version does not match what {userAgentBrowser} would normally use"
                });
            }

            if (AnalyzeGeoCorrelation(sink, sessionId))
            {
                anomalyCount++;
                anomalyLayers.Add("Geographic");
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "geo_mismatch_confidence", 0.3),
                    Weight = _configProvider.GetParameter(Name, "geo_mismatch_weight", 1.2),
                    Reason = "IP address location does not match the language preference claimed by the browser"
                });
            }

            var protocolClass = sink.ReadHint(SignalKeys.TransportProtocolClass);
            var isNonDocumentTraffic = protocolClass is "api" or "grpc" or "signalr";
            var isStreaming = sink.ReadBoolHint(SignalKeys.TransportIsStreaming);

            if (ipIsDatacenter && !string.IsNullOrEmpty(userAgentBrowser)
                && (userAgentBrowser.Contains("Chrome") || userAgentBrowser.Contains("Firefox")
                    || userAgentBrowser.Contains("Safari"))
                && !isNonDocumentTraffic && !isStreaming)
            {
                anomalyCount++;
                anomalyLayers.Add("IP-Browser");
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "datacenter_browser_confidence", 0.75),
                    Weight = _configProvider.GetParameter(Name, "datacenter_browser_weight", 1.9),
                    Reason = $"Datacenter IP with browser User-Agent: {userAgentBrowser}",
                    BotType = BotType.MaliciousBot.ToString()
                });
            }

            const int totalLayers = 5;
            var consistencyScore = 1.0 - (double)anomalyCount / totalLayers;
            sink.Raise($"{SignalKeys.CorrelationConsistencyScore}:{consistencyScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}", sessionId);
            sink.Raise($"{SignalKeys.CorrelationAnomalyCount}:{anomalyCount}", sessionId);
            sink.Raise($"correlation.anomaly_layers:{string.Join(",", anomalyLayers)}", sessionId);

            if (anomalyCount >= _configProvider.GetParameter(Name, "triple_anomaly_count", 3))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "triple_anomaly_confidence", 0.85),
                    Weight = _configProvider.GetParameter(Name, "triple_anomaly_weight", 2.0),
                    Reason = $"Multiple layer mismatches detected ({anomalyCount}/{totalLayers}): {string.Join(", ", anomalyLayers)}",
                    BotType = BotType.MaliciousBot.ToString()
                });
            }
            else if (anomalyCount >= _configProvider.GetParameter(Name, "double_anomaly_count", 2))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "double_anomaly_confidence", 0.6),
                    Weight = _configProvider.GetParameter(Name, "double_anomaly_weight", 1.5),
                    Reason = $"Cross-layer inconsistencies: {string.Join(", ", anomalyLayers)}",
                    BotType = BotType.Scraper.ToString()
                });
            }

            if (anomalyCount == 0 && !string.IsNullOrEmpty(tcpOsHint) && !string.IsNullOrEmpty(userAgentOs))
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = _configProvider.GetParameter(Name, "perfect_consistency_confidence", -0.25),
                    Weight = _configProvider.GetParameter(Name, "perfect_consistency_weight", 1.8),
                    Reason = "All signals consistent: operating system, browser, encryption, and location all match"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in multi-layer correlation analysis");
        }

        if (contributions.Count == 0)
        {
            contributions.Add(DetectionContribution.Info(
                Name, Category, "Cross-signal consistency check complete (not enough data to compare)"));
        }

        return Task.FromResult((IReadOnlyList<DetectionContribution>)contributions);
    }

    private bool AnalyzeOsCorrelation(SignalSink sink, string sessionId, string? tcpOsHint, string? tcpWindowOsHint, string? userAgentOs)
    {
        if (string.IsNullOrEmpty(tcpOsHint) && string.IsNullOrEmpty(tcpWindowOsHint)) return false;
        if (string.IsNullOrEmpty(userAgentOs)) return false;

        var networkOs = tcpOsHint ?? tcpWindowOsHint;
        sink.Raise($"correlation.network_os:{networkOs}", sessionId);
        sink.Raise($"correlation.claimed_os:{userAgentOs}", sessionId);

        var networkOsNorm = NormalizeOsName(networkOs);
        var userAgentOsNorm = NormalizeOsName(userAgentOs);

        var mismatch = !networkOsNorm.Equals(userAgentOsNorm, StringComparison.OrdinalIgnoreCase)
                       && !networkOsNorm.Contains(userAgentOsNorm, StringComparison.OrdinalIgnoreCase)
                       && !userAgentOsNorm.Contains(networkOsNorm, StringComparison.OrdinalIgnoreCase);

        sink.Raise($"{SignalKeys.CorrelationOsMismatch}:{(mismatch ? "true" : "false")}", sessionId);
        return mismatch;
    }

    private bool AnalyzeBrowserCorrelation(SignalSink sink, string sessionId, string? h2ClientType, string? userAgentBrowser)
    {
        if (string.IsNullOrEmpty(h2ClientType) || string.IsNullOrEmpty(userAgentBrowser)) return false;

        sink.Raise($"correlation.h2_client:{h2ClientType}", sessionId);
        sink.Raise($"correlation.claimed_browser:{userAgentBrowser}", sessionId);

        var browserNorm = NormalizeBrowserName(userAgentBrowser);
        var h2Norm = NormalizeBrowserName(h2ClientType);

        var mismatch = !h2Norm.Equals(browserNorm, StringComparison.OrdinalIgnoreCase)
                       && !h2Norm.Contains(browserNorm, StringComparison.OrdinalIgnoreCase)
                       && !h2ClientType.Contains("Bot")
                       && !string.IsNullOrEmpty(browserNorm);

        sink.Raise($"{SignalKeys.CorrelationBrowserMismatch}:{(mismatch ? "true" : "false")}", sessionId);
        return mismatch;
    }

    private static bool AnalyzeTlsCorrelation(SignalSink sink, string sessionId, string? tlsProtocol, string? userAgentBrowser)
    {
        if (string.IsNullOrEmpty(tlsProtocol) || string.IsNullOrEmpty(userAgentBrowser)) return false;

        var isModernBrowser = userAgentBrowser.Contains("Chrome")
                              || userAgentBrowser.Contains("Firefox")
                              || userAgentBrowser.Contains("Safari")
                              || userAgentBrowser.Contains("Edge");
        var isOldTls = tlsProtocol.Contains("Tls") && !tlsProtocol.Contains("Tls12") && !tlsProtocol.Contains("Tls13");

        var mismatch = isModernBrowser && isOldTls;
        sink.Raise($"correlation.tls_browser_mismatch:{(mismatch ? "true" : "false")}", sessionId);
        return mismatch;
    }

    private bool AnalyzeGeoCorrelation(SignalSink sink, string sessionId)
    {
        var ipCountry = sink.ReadHint("geo.country_code");
        var context = _httpContextAccessor.HttpContext;
        var acceptLanguage = context?.Request.Headers.AcceptLanguage.ToString() ?? string.Empty;

        if (string.IsNullOrEmpty(ipCountry) || string.IsNullOrEmpty(acceptLanguage)) return false;

        sink.Raise($"correlation.ip_country:{ipCountry}", sessionId);
        sink.Raise($"correlation.accept_language:{acceptLanguage}", sessionId);

        var primaryLang = acceptLanguage.Split(',')[0].Split(';')[0].Trim();
        sink.Raise($"correlation.primary_language:{primaryLang}", sessionId);

        if (!LanguageCountryMap.TryGetValue(ipCountry, out var expectedLanguages)) return false;

        var mismatch = !expectedLanguages.Any(lang => primaryLang.StartsWith(lang, StringComparison.OrdinalIgnoreCase));
        sink.Raise($"correlation.geo_mismatch:{(mismatch ? "true" : "false")}", sessionId);
        return mismatch;
    }

    private static string NormalizeOsName(string? os)
    {
        if (string.IsNullOrEmpty(os)) return string.Empty;
        os = os.ToLowerInvariant();
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
        if (browser.Contains("edg")) return "edge";
        if (browser.Contains("opera") || browser.Contains("opr")) return "opera";
        if (browser.Contains("chrome")) return "chrome";
        if (browser.Contains("firefox")) return "firefox";
        if (browser.Contains("safari")) return "safari";
        return browser;
    }
}
