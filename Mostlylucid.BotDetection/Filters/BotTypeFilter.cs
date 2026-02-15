using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Shared bot type filtering logic used by both MVC attributes and minimal API endpoint filters.
///     Centralizes the allow/block decision for bot types and network conditions.
/// </summary>
public static class BotTypeFilter
{
    /// <summary>
    ///     Checks whether a detected bot type should be allowed through based on the allow flags.
    ///     All types default to blocked. Scrapers and MaliciousBot are blocked by default
    ///     but CAN be allowed (useful for honeypots, research endpoints, etc.).
    /// </summary>
    public static bool IsBotTypeAllowed(
        BotType? botType,
        bool allowVerifiedBots = false,
        bool allowSearchEngines = false,
        bool allowSocialMediaBots = false,
        bool allowMonitoringBots = false,
        bool allowAiBots = false,
        bool allowGoodBots = false,
        bool allowScrapers = false,
        bool allowMaliciousBots = false) => botType switch
    {
        BotType.VerifiedBot => allowVerifiedBots || allowSearchEngines,
        BotType.SearchEngine => allowSearchEngines,
        BotType.SocialMediaBot => allowSocialMediaBots,
        BotType.MonitoringBot => allowMonitoringBots,
        BotType.AiBot => allowAiBots,
        BotType.GoodBot => allowGoodBots,
        BotType.Scraper => allowScrapers,
        BotType.MaliciousBot => allowMaliciousBots,
        _ => false // Unknown always blocked
    };

    /// <summary>
    ///     Checks whether a request should be blocked based on geographic/network conditions.
    ///     Reads signals from AggregatedEvidence on HttpContext, or from upstream headers.
    ///     Returns true if the request should be BLOCKED.
    /// </summary>
    public static bool IsBlockedByNetwork(
        HttpContext context,
        string? blockCountries = null,
        string? allowCountries = null,
        bool blockVpn = false,
        bool blockProxy = false,
        bool blockDatacenter = false,
        bool blockTor = false)
    {
        // No network restrictions configured
        if (string.IsNullOrEmpty(blockCountries) && string.IsNullOrEmpty(allowCountries)
            && !blockVpn && !blockProxy && !blockDatacenter && !blockTor)
            return false;

        // Try to read signals from AggregatedEvidence (local detection path)
        string? countryCode = null;
        bool isVpn = false, isProxy = false, isTor = false, isDatacenter = false;

        if (context.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var evidenceObj)
            && evidenceObj is AggregatedEvidence evidence && evidence.Signals is { Count: > 0 })
        {
            countryCode = GetSignalString(evidence.Signals, SignalKeys.GeoCountryCode);
            isVpn = GetSignalBool(evidence.Signals, SignalKeys.GeoIsVpn);
            isProxy = GetSignalBool(evidence.Signals, SignalKeys.GeoIsProxy);
            isTor = GetSignalBool(evidence.Signals, SignalKeys.GeoIsTor);
            isDatacenter = GetSignalBool(evidence.Signals, SignalKeys.GeoIsHosting)
                           || GetSignalBool(evidence.Signals, SignalKeys.IpIsDatacenter);
        }
        else
        {
            // Fallback: read from upstream forwarded headers (gateway trust path)
            countryCode = context.Request.Headers["X-Bot-Detection-Country"].FirstOrDefault();

            // Upstream network flags (if gateway forwards them)
            var flags = context.Request.Headers["X-Bot-Detection-NetworkFlags"].FirstOrDefault();
            if (!string.IsNullOrEmpty(flags))
            {
                isVpn = flags.Contains("vpn", StringComparison.OrdinalIgnoreCase);
                isProxy = flags.Contains("proxy", StringComparison.OrdinalIgnoreCase);
                isTor = flags.Contains("tor", StringComparison.OrdinalIgnoreCase);
                isDatacenter = flags.Contains("datacenter", StringComparison.OrdinalIgnoreCase)
                               || flags.Contains("hosting", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Country whitelist mode: if allowCountries is set, only those countries pass
        if (!string.IsNullOrEmpty(allowCountries) && !string.IsNullOrEmpty(countryCode))
        {
            var allowed = allowCountries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!allowed.Any(c => c.Equals(countryCode, StringComparison.OrdinalIgnoreCase)))
                return true; // Country not in whitelist → block
        }

        // Country blacklist: block specific countries
        if (!string.IsNullOrEmpty(blockCountries) && !string.IsNullOrEmpty(countryCode))
        {
            var blocked = blockCountries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (blocked.Any(c => c.Equals(countryCode, StringComparison.OrdinalIgnoreCase)))
                return true; // Country is in blacklist → block
        }

        // Network type blocking
        if (blockVpn && isVpn) return true;
        if (blockProxy && isProxy) return true;
        if (blockTor && isTor) return true;
        if (blockDatacenter && isDatacenter) return true;

        return false;
    }

    private static string? GetSignalString(IReadOnlyDictionary<string, object> signals, string key)
    {
        return signals.TryGetValue(key, out var val) && val is string s ? s : null;
    }

    private static bool GetSignalBool(IReadOnlyDictionary<string, object> signals, string key)
    {
        if (!signals.TryGetValue(key, out var val)) return false;
        return val is true or "true" or "True";
    }
}
