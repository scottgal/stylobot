namespace Mostlylucid.GeoDetection.Models;

/// <summary>
///     Geographic location information for an IP address
/// </summary>
public class GeoLocation
{
    /// <summary>
    ///     ISO 3166-1 alpha-2 country code (e.g., "US", "GB", "CN")
    /// </summary>
    public string CountryCode { get; set; } = "";

    /// <summary>
    ///     Full country name (e.g., "United States", "United Kingdom")
    /// </summary>
    public string CountryName { get; set; } = "";

    /// <summary>
    ///     Continent code (e.g., "NA", "EU", "AS")
    /// </summary>
    public string? ContinentCode { get; set; }

    /// <summary>
    ///     Region/state code (e.g., "CA" for California)
    /// </summary>
    public string? RegionCode { get; set; }

    /// <summary>
    ///     City name
    /// </summary>
    public string? City { get; set; }

    /// <summary>
    ///     Latitude
    /// </summary>
    public double? Latitude { get; set; }

    /// <summary>
    ///     Longitude
    /// </summary>
    public double? Longitude { get; set; }

    /// <summary>
    ///     Time zone (e.g., "America/New_York")
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    ///     Whether this is a known VPN/proxy
    /// </summary>
    public bool IsVpn { get; set; }

    /// <summary>
    ///     Whether this is from a datacenter/hosting provider
    /// </summary>
    public bool IsHosting { get; set; }
}