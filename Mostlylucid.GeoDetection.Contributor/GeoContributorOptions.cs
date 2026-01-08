namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Configuration options for the geo-detection contributor.
/// </summary>
public class GeoContributorOptions
{
    /// <summary>
    ///     Enable verification that known bots (Googlebot, Bingbot, etc.)
    ///     originate from expected countries/regions.
    ///     Default: true
    /// </summary>
    public bool EnableBotVerification { get; set; } = true;

    /// <summary>
    ///     Enable geo-inconsistency detection (locale/timezone mismatches).
    ///     Default: true
    /// </summary>
    public bool EnableInconsistencyDetection { get; set; } = true;

    /// <summary>
    ///     Countries to flag as suspicious (higher bot probability weight).
    ///     Use ISO 3166-1 alpha-2 codes (e.g., "CN", "RU", "KP").
    ///     Empty = don't flag any countries specifically.
    /// </summary>
    public List<string> SuspiciousCountries { get; set; } = [];

    /// <summary>
    ///     Countries to treat as trusted (lower weight for bot signals).
    ///     Use ISO 3166-1 alpha-2 codes.
    /// </summary>
    public List<string> TrustedCountries { get; set; } = [];

    /// <summary>
    ///     Whether to flag all hosting/datacenter IPs as suspicious.
    ///     Default: true (most real users don't browse from datacenters)
    /// </summary>
    public bool FlagHostingIps { get; set; } = true;

    /// <summary>
    ///     Whether to flag all VPN/proxy IPs.
    ///     Default: false (many legitimate users use VPNs)
    /// </summary>
    public bool FlagVpnIps { get; set; } = false;

    /// <summary>
    ///     Confidence boost for verified legitimate bots from expected countries.
    ///     Default: 0.3 (reduces bot score by this amount)
    /// </summary>
    public double VerifiedBotConfidenceBoost { get; set; } = 0.3;

    /// <summary>
    ///     Confidence penalty for bot origin mismatches.
    ///     Default: 0.8 (high confidence this is a fake bot)
    /// </summary>
    public double BotOriginMismatchPenalty { get; set; } = 0.8;

    /// <summary>
    ///     Weight multiplier for geo-based signals.
    ///     Default: 1.2
    /// </summary>
    public double SignalWeight { get; set; } = 1.2;

    /// <summary>
    ///     Priority for the geo contributor (lower = runs earlier).
    ///     Default: 15 (run after IP contributor but before inconsistency checks)
    /// </summary>
    public int Priority { get; set; } = 15;

    /// <summary>
    ///     Enable client-side geo collection via <client-geo /> tag helper.
    ///     Collects browser timezone, locale, and optionally GPS coordinates.
    ///     Default: true
    /// </summary>
    public bool EnableClientGeo { get; set; } = true;
}
