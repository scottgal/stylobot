using Mostlylucid.BotDetection.Dashboard;

namespace Mostlylucid.BotDetection.Orchestration.PiiManagement;

/// <summary>
///     Helper for emitting PII-related signals in a consistent, privacy-safe manner.
///     CRITICAL PRINCIPLES:
///     - PII NEVER goes in signal payloads
///     - Signals contain ONLY boolean indicators or hashed values
///     - Raw PII is accessed ONLY from BlackboardState directly
/// </summary>
public static class PiiSignalHelper
{
    /// <summary>
    ///     Emits IP-related signals WITHOUT putting raw IP in payload.
    ///     Signals emitted:
    ///     - "ip.available" → true (boolean indicator)
    ///     - "ip.detected" → hashed IP (non-reversible signature)
    ///     - "ip.subnet" → hashed subnet (for network-level correlation)
    /// </summary>
    public static Dictionary<string, object> EmitIpSignals(string? ipAddress, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(ipAddress))
        {
            // Boolean indicator - IP is available in state
            signals["ip.available"] = true;

            // Hashed signatures - safe to put in signals
            signals["ip.detected"] = hasher.HashIp(ipAddress);
            signals["ip.subnet"] = hasher.HashIpSubnet(ipAddress);
        }
        else
        {
            signals["ip.available"] = false;
        }

        return signals;
    }

    /// <summary>
    ///     Emits user agent signals WITHOUT putting raw UA in payload.
    ///     Signals emitted:
    ///     - "ua.available" → true (boolean indicator)
    ///     - "ua.detected" → hashed UA (non-reversible signature)
    /// </summary>
    public static Dictionary<string, object> EmitUserAgentSignals(string? userAgent, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(userAgent))
        {
            signals["ua.available"] = true;
            signals["ua.detected"] = hasher.HashUserAgent(userAgent);
        }
        else
        {
            signals["ua.available"] = false;
        }

        return signals;
    }

    /// <summary>
    ///     Emits geographic signals WITHOUT putting raw location in payload.
    ///     Signals emitted:
    ///     - "geo.available" → true (boolean indicator)
    ///     - "geo.country_code" → actual country code (NOT PII per GDPR)
    ///     - "geo.location_hash" → hashed location (for city-level privacy)
    ///     - "geo.timezone" → timezone (NOT PII, useful for behavioral analysis)
    /// </summary>
    public static Dictionary<string, object> EmitGeoSignals(GeoLocationData? geoData, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (geoData == null)
        {
            signals["geo.available"] = false;
            return signals;
        }

        signals["geo.available"] = true;

        // Country code is NOT PII (GDPR Article 4)
        if (!string.IsNullOrEmpty(geoData.CountryCode)) signals["geo.country_code"] = geoData.CountryCode;

        // Timezone is NOT PII but useful for behavioral analysis
        if (!string.IsNullOrEmpty(geoData.Timezone)) signals["geo.timezone"] = geoData.Timezone;

        // City/region/coordinates ARE PII - hash them
        if (!string.IsNullOrEmpty(geoData.City) || !string.IsNullOrEmpty(geoData.Region))
        {
            var locationString = $"{geoData.CountryCode}:{geoData.Region}:{geoData.City}";
            signals["geo.location_hash"] = hasher.HashLocation(locationString);
        }

        // GPS coordinates are DEFINITELY PII - hash if present
        if (geoData.Latitude.HasValue && geoData.Longitude.HasValue)
        {
            var coordString = $"{geoData.Latitude:F4},{geoData.Longitude:F4}";
            signals["geo.coordinates_hash"] = hasher.HashLocation(coordString);
        }

        return signals;
    }

    /// <summary>
    ///     Emits session-related signals WITHOUT putting session ID in payload.
    ///     Signals emitted:
    ///     - "session.available" → true (boolean indicator)
    ///     - "session.detected" → hashed session ID
    /// </summary>
    public static Dictionary<string, object> EmitSessionSignals(string? sessionId, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(sessionId))
        {
            signals["session.available"] = true;
            signals["session.detected"] = hasher.ComputeSignature(sessionId);
        }
        else
        {
            signals["session.available"] = false;
        }

        return signals;
    }

    /// <summary>
    ///     Emits locale signals WITHOUT putting raw headers in payload.
    ///     Signals emitted:
    ///     - "locale.available" → true (boolean indicator)
    ///     - "locale.language" → primary language (NOT PII, useful for bot detection)
    ///     - "locale.detected" → hashed full Accept-Language header
    /// </summary>
    public static Dictionary<string, object> EmitLocaleSignals(string? acceptLanguage, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (string.IsNullOrEmpty(acceptLanguage))
        {
            signals["locale.available"] = false;
            return signals;
        }

        signals["locale.available"] = true;

        // Primary language is NOT PII (common, non-identifying)
        var primaryLanguage = acceptLanguage.Split(',', ';')[0].Trim();
        signals["locale.language"] = primaryLanguage;

        // Full header might be identifying - hash it
        signals["locale.detected"] = hasher.ComputeSignature(acceptLanguage);

        return signals;
    }

    /// <summary>
    ///     Emits referer signals WITHOUT putting raw URL in payload.
    ///     Signals emitted:
    ///     - "referer.available" → true (boolean indicator)
    ///     - "referer.detected" → hashed referer (URLs can be PII)
    /// </summary>
    public static Dictionary<string, object> EmitRefererSignals(string? referer, PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(referer))
        {
            signals["referer.available"] = true;
            signals["referer.detected"] = hasher.ComputeSignature(referer);
        }
        else
        {
            signals["referer.available"] = false;
        }

        return signals;
    }

    /// <summary>
    ///     Emits composite request signature from multiple PII sources.
    ///     This signature is deterministic but non-reversible.
    ///     Signal emitted:
    ///     - "request.signature" → HMAC(IP|UA|Path)
    /// </summary>
    public static Dictionary<string, object> EmitRequestSignature(
        string? ipAddress,
        string? userAgent,
        string? path,
        PiiHasher hasher)
    {
        var signals = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(ipAddress) || !string.IsNullOrEmpty(userAgent))
            signals["request.signature"] = hasher.CreateRequestSignature(ipAddress, userAgent, path);

        return signals;
    }
}

/// <summary>
///     Standard signal key constants for PII-related signals.
/// </summary>
public static class PiiSignalKeys
{
    // IP signals
    public const string IpAvailable = "ip.available";
    public const string IpDetected = "ip.detected";
    public const string IpSubnet = "ip.subnet";

    // User agent signals
    public const string UaAvailable = "ua.available";
    public const string UaDetected = "ua.detected";

    // Geographic signals
    public const string GeoAvailable = "geo.available";
    public const string GeoCountryCode = "geo.country_code";
    public const string GeoLocationHash = "geo.location_hash";
    public const string GeoCoordinatesHash = "geo.coordinates_hash";
    public const string GeoTimezone = "geo.timezone";

    // Session signals
    public const string SessionAvailable = "session.available";
    public const string SessionDetected = "session.detected";

    // Locale signals
    public const string LocaleAvailable = "locale.available";
    public const string LocaleLanguage = "locale.language";
    public const string LocaleDetected = "locale.detected";

    // Referer signals
    public const string RefererAvailable = "referer.available";
    public const string RefererDetected = "referer.detected";

    // Request signature
    public const string RequestSignature = "request.signature";
}