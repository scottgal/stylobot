using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects inconsistencies between different request signals.
///     Analyzes: UA vs Accept-Language, UA vs platform claims, datacenter IP with residential UA.
///     These inconsistencies are common in bots that spoof user agents but miss other details.
/// </summary>
public partial class InconsistencyDetector : IDetector
{
    private readonly ILogger<InconsistencyDetector> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;

    public InconsistencyDetector(
        ILogger<InconsistencyDetector> logger,
        IOptions<BotDetectionOptions> options,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _options = options.Value;
        _metrics = metrics;
    }

    public string Name => "Inconsistency Detector";

    /// <summary>Stage 2: Meta-analysis - reads all signals from stages 0 and 1</summary>
    public DetectorStage Stage => DetectorStage.MetaAnalysis;

    public Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();

        try
        {
            var headers = context.Request.Headers;
            var userAgent = headers.UserAgent.ToString();
            var acceptLanguage = headers.AcceptLanguage.ToString();
            var accept = headers.Accept.ToString();

            // Parse UA to extract claims
            var uaClaims = ParseUserAgentClaims(userAgent);

            // === Check 1: UA claims mobile but missing mobile signals ===
            if (uaClaims.ClaimsMobile)
            {
                var hasTouchHeader = headers.ContainsKey("Sec-CH-UA-Mobile");
                var acceptsTouch = accept.Contains("touch", StringComparison.OrdinalIgnoreCase);

                // Mobile UA without typical mobile behaviors
                if (string.IsNullOrEmpty(acceptLanguage))
                {
                    result.Confidence += 0.15;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "Inconsistency",
                        Detail = "Mobile UA without Accept-Language header (real mobile browsers always send this)",
                        ConfidenceImpact = 0.15
                    });
                }
            }

            // === Check 2: Windows/Mac UA but no Accept-Language ===
            if ((uaClaims.ClaimsWindows || uaClaims.ClaimsMac) && string.IsNullOrEmpty(acceptLanguage))
            {
                result.Confidence += 0.2;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Inconsistency",
                    Detail = "Desktop UA without Accept-Language (all major browsers send this)",
                    ConfidenceImpact = 0.2
                });
            }

            // === Check 3: Browser claims vs headers ===
            if (uaClaims.ClaimsChrome && !headers.ContainsKey("Sec-Fetch-Mode") &&
                !headers.ContainsKey("sec-ch-ua"))
                // Modern Chrome always sends these headers
                if (uaClaims.ChromeVersion >= 73)
                {
                    result.Confidence += 0.15;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "Inconsistency",
                        Detail = $"Claims Chrome {uaClaims.ChromeVersion} but missing modern Chrome headers",
                        ConfidenceImpact = 0.15
                    });
                }

            // === Check 4: Accept-Language vs UA language hints ===
            if (!string.IsNullOrEmpty(acceptLanguage) && !string.IsNullOrEmpty(userAgent))
            {
                var inconsistency = CheckLanguageConsistency(userAgent, acceptLanguage);
                if (inconsistency != null)
                {
                    result.Confidence += 0.1;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "Inconsistency",
                        Detail = inconsistency,
                        ConfidenceImpact = 0.1
                    });
                }
            }

            // === Check 5: Generic Accept header with specific browser UA ===
            if (accept == "*/*" && (uaClaims.ClaimsChrome || uaClaims.ClaimsFirefox || uaClaims.ClaimsSafari))
            {
                result.Confidence += 0.2;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Inconsistency",
                    Detail = $"Claims {uaClaims.BrowserName} but sends generic */* Accept header",
                    ConfidenceImpact = 0.2
                });
            }

            // === Check 6: HTTP/2 claims but using HTTP/1.1 features ===
            var connection = headers.Connection.ToString().ToLowerInvariant();
            if (connection == "keep-alive" && uaClaims.ClaimsChrome && uaClaims.ChromeVersion >= 90)
            {
                // Modern Chrome uses HTTP/2+ by default, doesn't need Connection header
                // (This is a minor signal)
                result.Confidence += 0.05;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Inconsistency",
                    Detail = "Modern browser sending HTTP/1.1 Connection header",
                    ConfidenceImpact = 0.05
                });
            }

            // === Check 7: Referer inconsistencies ===
            var referer = headers.Referer.ToString();
            if (!string.IsNullOrEmpty(referer))
                // Check if referer domain makes sense
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                    // Suspicious: Referer from localhost or internal IPs
                    if (refererUri.Host == "localhost" || refererUri.Host.StartsWith("192.168.") ||
                        refererUri.Host.StartsWith("10.") || refererUri.Host.StartsWith("172."))
                    {
                        result.Confidence += 0.3;
                        result.Reasons.Add(new DetectionReason
                        {
                            Category = "Inconsistency",
                            Detail = "Referer points to internal/localhost address",
                            ConfidenceImpact = 0.3
                        });
                    }

            // === Check 8: Bot UA with residential-looking accept headers ===
            if (IsBotUserAgent(userAgent) && !string.IsNullOrEmpty(acceptLanguage) &&
                accept.Contains("text/html"))
            {
                // Bots typically don't need full browser Accept headers
                result.Confidence += 0.1;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "Inconsistency",
                    Detail = "Bot UA with full browser Accept/Accept-Language headers",
                    ConfidenceImpact = 0.1
                });
            }

            // Cap confidence
            result.Confidence = Math.Min(1.0, result.Confidence);

            // Set bot type if significant inconsistencies found
            if (result.Confidence >= 0.3) result.BotType = BotType.Scraper;

            stopwatch.Stop();
            _metrics?.RecordDetection(result.Confidence, result.Confidence > _options.BotThreshold, stopwatch.Elapsed,
                Name);

            if (result.Reasons.Count > 0)
                _logger.LogDebug(
                    "Inconsistency detection found {Count} issues, total score: {Score:F2}",
                    result.Reasons.Count, result.Confidence);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Inconsistency detection failed");
            _metrics?.RecordError(Name, ex.GetType().Name);
        }

        return Task.FromResult(result);
    }

    private static UserAgentClaims ParseUserAgentClaims(string userAgent)
    {
        var claims = new UserAgentClaims();
        var ua = userAgent.ToLowerInvariant();

        // Mobile detection
        claims.ClaimsMobile = ua.Contains("mobile") || ua.Contains("android") ||
                              ua.Contains("iphone") || ua.Contains("ipad");

        // OS detection
        claims.ClaimsWindows = ua.Contains("windows");
        claims.ClaimsMac = ua.Contains("macintosh") || ua.Contains("mac os");
        claims.ClaimsLinux = ua.Contains("linux") && !ua.Contains("android");

        // Browser detection
        claims.ClaimsChrome = ua.Contains("chrome") && !ua.Contains("edg");
        claims.ClaimsFirefox = ua.Contains("firefox");
        claims.ClaimsSafari = ua.Contains("safari") && !ua.Contains("chrome");
        claims.ClaimsEdge = ua.Contains("edg");

        // Chrome version
        if (claims.ClaimsChrome)
        {
            var match = ChromeVersionRegex().Match(userAgent);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var version)) claims.ChromeVersion = version;
        }

        // Browser name for logging
        claims.BrowserName = claims.ClaimsChrome ? "Chrome" :
            claims.ClaimsFirefox ? "Firefox" :
            claims.ClaimsSafari ? "Safari" :
            claims.ClaimsEdge ? "Edge" : "Unknown";

        return claims;
    }

    private static string? CheckLanguageConsistency(string userAgent, string acceptLanguage)
    {
        var ua = userAgent.ToLowerInvariant();
        var langs = acceptLanguage.ToLowerInvariant();

        // Check for Chinese bot claiming English UA
        if (ua.Contains("baidu") && !langs.Contains("zh")) return "Baidu bot with non-Chinese Accept-Language";

        // Russian search engine with non-Russian language
        if (ua.Contains("yandex") && !langs.Contains("ru")) return "Yandex bot with non-Russian Accept-Language";

        return null;
    }

    private static bool IsBotUserAgent(string userAgent)
    {
        var ua = userAgent.ToLowerInvariant();
        return ua.Contains("bot") || ua.Contains("crawler") || ua.Contains("spider") ||
               ua.Contains("scraper") || ua.Contains("curl") || ua.Contains("wget") ||
               ua.Contains("python") || ua.Contains("http");
    }

    [GeneratedRegex(@"Chrome/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ChromeVersionRegex();

    private record UserAgentClaims
    {
        public bool ClaimsMobile { get; set; }
        public bool ClaimsWindows { get; set; }
        public bool ClaimsMac { get; set; }
        public bool ClaimsLinux { get; set; }
        public bool ClaimsChrome { get; set; }
        public bool ClaimsFirefox { get; set; }
        public bool ClaimsSafari { get; set; }
        public bool ClaimsEdge { get; set; }
        public int ChromeVersion { get; set; }
        public string BrowserName { get; set; } = "Unknown";
    }
}