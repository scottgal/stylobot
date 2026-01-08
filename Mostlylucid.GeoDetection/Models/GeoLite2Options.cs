namespace Mostlylucid.GeoDetection.Models;

/// <summary>
///     Configuration options for GeoLocation services
/// </summary>
public class GeoLite2Options
{
    /// <summary>
    ///     Which provider to use for IP geolocation
    /// </summary>
    public GeoProvider Provider { get; set; } = GeoProvider.MaxMindLocal;

    /// <summary>
    ///     MaxMind account ID (required for auto-download)
    ///     Get a free account at https://www.maxmind.com/en/geolite2/signup
    /// </summary>
    public int? AccountId { get; set; }

    /// <summary>
    ///     MaxMind license key (required for auto-download)
    ///     Generate at https://www.maxmind.com/en/accounts/current/license-key
    /// </summary>
    public string? LicenseKey { get; set; }

    /// <summary>
    ///     Path to the GeoLite2-City.mmdb database file
    ///     If not specified, defaults to ./data/GeoLite2-City.mmdb
    /// </summary>
    public string DatabasePath { get; set; } = Path.Combine("data", "GeoLite2-City.mmdb");

    /// <summary>
    ///     Enable automatic database downloads and updates
    ///     Requires AccountId and LicenseKey to be set
    /// </summary>
    public bool EnableAutoUpdate { get; set; } = true;

    /// <summary>
    ///     How often to check for database updates (default: 24 hours)
    ///     GeoLite2 databases are updated weekly on Tuesdays
    /// </summary>
    public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     Download the database on startup if it doesn't exist
    /// </summary>
    public bool DownloadOnStartup { get; set; } = true;

    /// <summary>
    ///     Database type to download: City, Country, or ASN
    /// </summary>
    public GeoLite2DatabaseType DatabaseType { get; set; } = GeoLite2DatabaseType.City;

    /// <summary>
    ///     Cache duration for IP lookups (default: 1 hour)
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     Maximum cache entries (default: 10000)
    /// </summary>
    public int MaxCacheEntries { get; set; } = 10000;

    /// <summary>
    ///     Fall back to SimpleGeoLocationService if database is unavailable
    /// </summary>
    public bool FallbackToSimple { get; set; } = true;

    /// <summary>
    ///     Returns true if auto-download is properly configured
    /// </summary>
    public bool IsAutoDownloadConfigured => AccountId.HasValue && !string.IsNullOrEmpty(LicenseKey);
}

/// <summary>
///     GeoLite2 database types
/// </summary>
public enum GeoLite2DatabaseType
{
    /// <summary>
    ///     City-level precision with coordinates, timezone, etc.
    /// </summary>
    City,

    /// <summary>
    ///     Country-level precision only (smaller database)
    /// </summary>
    Country,

    /// <summary>
    ///     Autonomous System Number database
    /// </summary>
    ASN
}

/// <summary>
///     Available geo-IP providers
/// </summary>
public enum GeoProvider
{
    /// <summary>
    ///     Simple mock service for testing (no external dependencies)
    /// </summary>
    Simple,

    /// <summary>
    ///     MaxMind GeoLite2 local database (requires free account for download)
    ///     Pros: Fast, offline, accurate, city-level. Cons: Requires account, ~60MB database
    /// </summary>
    MaxMindLocal,

    /// <summary>
    ///     ip-api.com free API (no account required)
    ///     Pros: No setup, city-level detail. Cons: Rate limited (45/min), online only
    /// </summary>
    IpApi,

    /// <summary>
    ///     ipapi.co free API (no account required)
    ///     Pros: No setup, generous limits (30k/month). Cons: Online only
    /// </summary>
    IpApiCo,

    /// <summary>
    ///     DataHub GeoIP2-IPv4 free CSV database (no account required)
    ///     Pros: Local, free, no account, auto-updates weekly. Cons: Country-level only (~27MB)
    ///     Data from: https://datahub.io/core/geoip2-ipv4
    /// </summary>
    DataHubCsv
}

/// <summary>
///     Database cache options for storing geo lookups
/// </summary>
public class GeoCacheOptions
{
    /// <summary>
    ///     Enable database caching of geo lookups
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     SQLite connection string (default: local file)
    ///     Set to a different connection string for other EF providers
    /// </summary>
    public string ConnectionString { get; set; } = "Data Source=data/geocache.db";

    /// <summary>
    ///     How long to cache geo lookups in the database
    /// </summary>
    public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    ///     Clean up expired entries periodically
    /// </summary>
    public bool EnableCleanup { get; set; } = true;

    /// <summary>
    ///     How often to run cleanup (default: daily)
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromDays(1);
}