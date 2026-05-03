using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Signal keys for geo-location data.
///     Extends the partial SignalKeys class from BotDetection.
/// </summary>
public static partial class GeoSignalKeys
{
    // Geographic location signals
    public const string GeoCountryCode = "geo.country_code";
    public const string GeoCountryName = "geo.country_name";
    public const string GeoRegionCode = "geo.region_code";
    public const string GeoCity = "geo.city";
    public const string GeoLatitude = "geo.latitude";
    public const string GeoLongitude = "geo.longitude";
    public const string GeoTimezone = "geo.timezone";
    public const string GeoContinentCode = "geo.continent_code";

    // Geo-based risk signals
    public const string GeoIsVpn = "geo.is_vpn";
    public const string GeoIsHosting = "geo.is_hosting";
    public const string GeoIsProxy = "geo.is_proxy";
    public const string GeoIsTor = "geo.is_tor";
    public const string GeoIsSuspiciousCountry = "geo.is_suspicious_country";

    // Inconsistency signals
    public const string GeoInconsistencyDetected = "geo.inconsistency_detected";
    public const string GeoInconsistencyType = "geo.inconsistency_type";
    public const string GeoBotOriginMismatch = "geo.bot_origin_mismatch";
    public const string GeoTimezoneMismatch = "geo.timezone_mismatch";
    public const string GeoLocaleMismatch = "geo.locale_mismatch";

    // Verification signals
    public const string GeoBotExpectedCountry = "geo.bot_expected_country";
    public const string GeoBotActualCountry = "geo.bot_actual_country";
    public const string GeoBotVerified = "geo.bot_verified";
}
