using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Contributing detector that provides geographic location analysis.
///     Uses GeoDetection services to enrich requests with location data
///     and detect geo-based bot indicators.
/// </summary>
public class GeoContributor : ContributingDetectorBase
{
    private readonly IGeoLocationService _geoService;
    private readonly ILogger<GeoContributor> _logger;
    private readonly GeoContributorOptions _options;

    // Known bot origins - which countries should specific bots come from
    private static readonly Dictionary<string, string[]> KnownBotOrigins = new(StringComparer.OrdinalIgnoreCase)
    {
        // Google bots
        ["googlebot"] = ["US"],
        ["google"] = ["US"],
        ["mediapartners-google"] = ["US"],
        ["adsbot-google"] = ["US"],
        ["apis-google"] = ["US"],
        ["feedfetcher-google"] = ["US"],

        // Microsoft bots
        ["bingbot"] = ["US"],
        ["msnbot"] = ["US"],
        ["bingpreview"] = ["US"],
        ["adidxbot"] = ["US"],

        // Yahoo
        ["slurp"] = ["US"],

        // Russian search
        ["yandex"] = ["RU", "FI", "NL"], // Yandex uses RU, Finland, Netherlands
        ["yandexbot"] = ["RU", "FI", "NL"],

        // Chinese search
        ["baidu"] = ["CN", "HK"],
        ["baiduspider"] = ["CN", "HK"],
        ["sogou"] = ["CN"],

        // Apple
        ["applebot"] = ["US", "IE"], // US and Ireland

        // DuckDuckGo
        ["duckduckbot"] = ["US"],

        // Facebook
        ["facebookexternalhit"] = ["US", "IE"],
        ["facebot"] = ["US", "IE"],

        // Twitter
        ["twitterbot"] = ["US"],

        // LinkedIn
        ["linkedinbot"] = ["US", "IE"],

        // Amazon
        ["amazonbot"] = ["US"],

        // Archive.org
        ["ia_archiver"] = ["US"],

        // Pinterest
        ["pinterest"] = ["US"]
    };

    // Timezone to expected countries mapping (rough)
    private static readonly Dictionary<string, string[]> TimezoneCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["America/New_York"] = ["US", "CA"],
        ["America/Los_Angeles"] = ["US", "CA"],
        ["America/Chicago"] = ["US", "CA", "MX"],
        ["Europe/London"] = ["GB", "IE", "PT"],
        ["Europe/Paris"] = ["FR", "BE", "LU"],
        ["Europe/Berlin"] = ["DE", "AT", "CH"],
        ["Asia/Tokyo"] = ["JP"],
        ["Asia/Shanghai"] = ["CN"],
        ["Asia/Kolkata"] = ["IN"],
        ["Australia/Sydney"] = ["AU"],
        ["Pacific/Auckland"] = ["NZ"]
    };

    // Language code to expected countries
    private static readonly Dictionary<string, string[]> LanguageCountries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en-US"] = ["US", "CA", "AU", "NZ", "GB", "IE"],
        ["en-GB"] = ["GB", "IE", "AU", "NZ"],
        ["de-DE"] = ["DE", "AT", "CH"],
        ["fr-FR"] = ["FR", "BE", "CH", "CA"],
        ["es-ES"] = ["ES", "MX", "AR", "CO", "CL"],
        ["zh-CN"] = ["CN", "SG", "TW", "HK"],
        ["ja-JP"] = ["JP"],
        ["ko-KR"] = ["KR"],
        ["ru-RU"] = ["RU", "BY", "KZ"],
        ["pt-BR"] = ["BR", "PT"],
        ["it-IT"] = ["IT", "CH"]
    };

    public GeoContributor(
        IGeoLocationService geoService,
        IOptions<GeoContributorOptions> options,
        ILogger<GeoContributor> logger)
    {
        _geoService = geoService;
        _options = options.Value;
        _logger = logger;
    }

    public override string Name => "Geo";
    public override int Priority => _options.Priority;

    // No triggers - runs in first wave alongside IP contributor
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    public override async Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var clientIp = state.ClientIp;

        if (string.IsNullOrEmpty(clientIp))
        {
            _logger.LogDebug("No client IP available for geo detection");
            return None();
        }

        // Skip local/loopback addresses
        if (IsLocalIp(clientIp))
        {
            _logger.LogDebug("Skipping geo lookup for local IP: {Ip}", clientIp);
            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Geo",
                ConfidenceDelta = 0,
                Weight = 0,
                Reason = "Local/loopback IP - geo lookup skipped",
                Signals = ImmutableDictionary<string, object>.Empty
                    .Add(GeoSignalKeys.GeoCountryCode, "LOCAL")
            });
        }

        // Get geo location
        var location = await _geoService.GetLocationAsync(clientIp, cancellationToken);

        if (location == null)
        {
            _logger.LogDebug("Geo lookup returned null for IP: {Ip}", MaskIp(clientIp));
            return Single(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Geo",
                ConfidenceDelta = 0.1, // Slight bot indicator - can't resolve location
                Weight = 0.3,
                Reason = "Could not resolve geographic location"
            });
        }

        // Build signals from location
        var signals = BuildLocationSignals(location);

        // Add base geo contribution
        contributions.Add(new DetectionContribution
        {
            DetectorName = Name,
            Category = "Geo",
            ConfidenceDelta = 0,
            Weight = 0,
            Reason = $"Location: {location.City ?? location.RegionCode ?? location.CountryCode}, {location.CountryName}",
            Signals = signals
        });

        // Check for suspicious country
        if (_options.SuspiciousCountries.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
        {
            contributions.Add(DetectionContribution.Bot(
                    Name, "Geo", 0.3,
                    $"Request from suspicious country: {location.CountryName}",
                    _options.SignalWeight,
                    nameof(BotType.Unknown))
                with
                {
                    Signals = signals.Add(GeoSignalKeys.GeoIsSuspiciousCountry, true)
                });
        }

        // Check for hosting/datacenter IP
        if (location.IsHosting && _options.FlagHostingIps)
        {
            contributions.Add(DetectionContribution.Bot(
                    Name, "Geo", 0.5,
                    $"Request from hosting/datacenter IP in {location.CountryName}",
                    _options.SignalWeight,
                    nameof(BotType.Unknown))
                with
                {
                    Signals = signals.Add(GeoSignalKeys.GeoIsHosting, true)
                });
        }

        // Check for VPN
        if (location.IsVpn && _options.FlagVpnIps)
        {
            contributions.Add(DetectionContribution.Bot(
                    Name, "Geo", 0.2,
                    $"Request from VPN/proxy in {location.CountryName}",
                    0.5, // Lower weight - VPNs are common
                    nameof(BotType.Unknown))
                with
                {
                    Signals = signals.Add(GeoSignalKeys.GeoIsVpn, true)
                });
        }

        // Bot verification if enabled
        if (_options.EnableBotVerification)
        {
            var botVerification = VerifyBotOrigin(state, location, signals);
            if (botVerification != null)
            {
                contributions.Add(botVerification);
            }
        }

        // Inconsistency detection if enabled
        if (_options.EnableInconsistencyDetection)
        {
            var inconsistencies = DetectInconsistencies(state, location, signals);
            contributions.AddRange(inconsistencies);
        }

        return contributions;
    }

    private ImmutableDictionary<string, object> BuildLocationSignals(Models.GeoLocation location)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object>();

        builder.Add(GeoSignalKeys.GeoCountryCode, location.CountryCode);
        builder.Add(GeoSignalKeys.GeoCountryName, location.CountryName);

        if (!string.IsNullOrEmpty(location.ContinentCode))
            builder.Add(GeoSignalKeys.GeoContinentCode, location.ContinentCode);

        if (!string.IsNullOrEmpty(location.RegionCode))
            builder.Add(GeoSignalKeys.GeoRegionCode, location.RegionCode);

        if (!string.IsNullOrEmpty(location.City))
            builder.Add(GeoSignalKeys.GeoCity, location.City);

        if (location.Latitude.HasValue)
            builder.Add(GeoSignalKeys.GeoLatitude, location.Latitude.Value);

        if (location.Longitude.HasValue)
            builder.Add(GeoSignalKeys.GeoLongitude, location.Longitude.Value);

        if (!string.IsNullOrEmpty(location.TimeZone))
            builder.Add(GeoSignalKeys.GeoTimezone, location.TimeZone);

        builder.Add(GeoSignalKeys.GeoIsVpn, location.IsVpn);
        builder.Add(GeoSignalKeys.GeoIsHosting, location.IsHosting);

        return builder.ToImmutable();
    }

    private DetectionContribution? VerifyBotOrigin(
        BlackboardState state,
        Models.GeoLocation location,
        ImmutableDictionary<string, object> signals)
    {
        var userAgent = state.UserAgent.ToLowerInvariant();

        foreach (var (botPattern, expectedCountries) in KnownBotOrigins)
        {
            if (!userAgent.Contains(botPattern))
                continue;

            var isFromExpectedCountry = expectedCountries.Contains(
                location.CountryCode,
                StringComparer.OrdinalIgnoreCase);

            if (isFromExpectedCountry)
            {
                // Bot claims to be from expected country - could be legitimate
                _logger.LogDebug(
                    "Bot {BotPattern} verified from expected country {Country}",
                    botPattern, location.CountryCode);

                return new DetectionContribution
                {
                    DetectorName = Name,
                    Category = "Geo-BotVerification",
                    ConfidenceDelta = -_options.VerifiedBotConfidenceBoost, // Reduces bot probability
                    Weight = _options.SignalWeight,
                    Reason = $"Bot '{botPattern}' verified from expected country {location.CountryCode}",
                    Signals = signals
                        .Add(GeoSignalKeys.GeoBotVerified, true)
                        .Add(GeoSignalKeys.GeoBotExpectedCountry, string.Join(",", expectedCountries))
                        .Add(GeoSignalKeys.GeoBotActualCountry, location.CountryCode)
                };
            }

            // Bot claims identity but is from unexpected country - HIGHLY suspicious
            _logger.LogWarning(
                "Bot origin mismatch: {BotPattern} claimed but IP from {Country} (expected: {Expected})",
                botPattern, location.CountryCode, string.Join(",", expectedCountries));

            return DetectionContribution.Bot(
                    Name, "Geo-BotVerification",
                    _options.BotOriginMismatchPenalty,
                    $"FAKE BOT: '{botPattern}' User-Agent from {location.CountryName} (expected: {string.Join("/", expectedCountries)})",
                    2.0, // High weight - this is a strong indicator
                    nameof(BotType.Scraper))
                with
                {
                    Signals = signals
                        .Add(GeoSignalKeys.GeoBotOriginMismatch, true)
                        .Add(GeoSignalKeys.GeoBotExpectedCountry, string.Join(",", expectedCountries))
                        .Add(GeoSignalKeys.GeoBotActualCountry, location.CountryCode)
                        .Add(GeoSignalKeys.GeoBotVerified, false)
                };
        }

        return null;
    }

    private List<DetectionContribution> DetectInconsistencies(
        BlackboardState state,
        Models.GeoLocation location,
        ImmutableDictionary<string, object> signals)
    {
        var inconsistencies = new List<DetectionContribution>();
        var headers = state.HttpContext.Request.Headers;

        // Check Accept-Language vs geo location
        var acceptLanguage = headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(acceptLanguage))
        {
            var primaryLanguage = acceptLanguage.Split(',').FirstOrDefault()?.Split(';').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(primaryLanguage) &&
                LanguageCountries.TryGetValue(primaryLanguage, out var expectedCountries))
            {
                if (!expectedCountries.Contains(location.CountryCode, StringComparer.OrdinalIgnoreCase))
                {
                    // Language/location mismatch - mild indicator (travelers, expats exist)
                    inconsistencies.Add(new DetectionContribution
                    {
                        DetectorName = Name,
                        Category = "Geo-Inconsistency",
                        ConfidenceDelta = 0.15,
                        Weight = 0.7,
                        Reason = $"Language '{primaryLanguage}' from unexpected country {location.CountryCode}",
                        Signals = signals
                            .Add(GeoSignalKeys.GeoLocaleMismatch, true)
                            .Add(GeoSignalKeys.GeoInconsistencyType, "locale")
                    });
                }
            }
        }

        // Check client hints timezone if available
        var timezoneHint = headers["Sec-CH-UA-Platform"].ToString();
        // Note: Real timezone would come from client-side JS, which we may not have

        // Check for datacenter IP + consumer browser pattern (already covered by IP contributor,
        // but we can add geo context)
        var isDatacenter = state.GetSignal<bool>(SignalKeys.IpIsDatacenter);
        var userAgent = state.UserAgent;

        if (location.IsHosting && LooksLikeBrowser(userAgent) &&
            !string.IsNullOrEmpty(acceptLanguage))
        {
            // Consumer browser from datacenter with specific locale
            inconsistencies.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "Geo-Inconsistency",
                ConfidenceDelta = 0.4,
                Weight = 1.3,
                Reason = $"Consumer browser with locale '{acceptLanguage.Split(',')[0]}' from datacenter in {location.CountryName}",
                Signals = signals
                    .Add(GeoSignalKeys.GeoInconsistencyDetected, true)
                    .Add(GeoSignalKeys.GeoInconsistencyType, "datacenter-consumer")
            });
        }

        return inconsistencies;
    }

    private static bool LooksLikeBrowser(string userAgent) =>
        userAgent.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase) &&
        (userAgent.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
         userAgent.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ||
         userAgent.Contains("Safari", StringComparison.OrdinalIgnoreCase) ||
         userAgent.Contains("Edge", StringComparison.OrdinalIgnoreCase));

    private static bool IsLocalIp(string ip)
    {
        if (string.IsNullOrEmpty(ip))
            return true;

        return ip.StartsWith("127.") ||
               ip.StartsWith("10.") ||
               ip.StartsWith("192.168.") ||
               ip.StartsWith("172.16.") ||
               ip.StartsWith("172.17.") ||
               ip.StartsWith("172.18.") ||
               ip.StartsWith("172.19.") ||
               ip.StartsWith("172.20.") ||
               ip.StartsWith("172.21.") ||
               ip.StartsWith("172.22.") ||
               ip.StartsWith("172.23.") ||
               ip.StartsWith("172.24.") ||
               ip.StartsWith("172.25.") ||
               ip.StartsWith("172.26.") ||
               ip.StartsWith("172.27.") ||
               ip.StartsWith("172.28.") ||
               ip.StartsWith("172.29.") ||
               ip.StartsWith("172.30.") ||
               ip.StartsWith("172.31.") ||
               ip == "::1" ||
               ip.StartsWith("fe80:") ||
               ip.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string MaskIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";

        if (ip.Length > 10)
            return ip[..10] + "...";

        return ip;
    }
}
