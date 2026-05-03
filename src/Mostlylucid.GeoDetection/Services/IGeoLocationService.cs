using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     Service for looking up geographic location of IP addresses
/// </summary>
public interface IGeoLocationService
{
    /// <summary>
    ///     Get geographic location for an IP address
    /// </summary>
    Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Check if an IP is from a specific country
    /// </summary>
    Task<bool> IsFromCountryAsync(string ipAddress, string countryCode, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get statistics about geo lookups
    /// </summary>
    GeoLocationStatistics GetStatistics();
}

/// <summary>
///     Simple in-memory geo location service (placeholder for MaxMind integration)
/// </summary>
public class SimpleGeoLocationService : IGeoLocationService
{
    private readonly Dictionary<string, GeoLocation> _cache = new();
    private readonly GeoLocationStatistics _stats = new();

    public Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        // Simple placeholder - in production this would use MaxMind GeoLite2 database
        // For now, return a mock result based on IP pattern

        _stats.TotalLookups++;

        if (_cache.TryGetValue(ipAddress, out var cached))
        {
            _stats.CacheHits++;
            return Task.FromResult<GeoLocation?>(cached);
        }

        // Mock geo data based on IP ranges (for demo purposes)
        var location = GenerateMockLocation(ipAddress);

        _cache[ipAddress] = location;
        return Task.FromResult<GeoLocation?>(location);
    }

    public async Task<bool> IsFromCountryAsync(string ipAddress, string countryCode,
        CancellationToken cancellationToken = default)
    {
        var location = await GetLocationAsync(ipAddress, cancellationToken);
        return location?.CountryCode.Equals(countryCode, StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public GeoLocationStatistics GetStatistics()
    {
        return new GeoLocationStatistics
        {
            TotalLookups = _stats.TotalLookups,
            CacheHits = _stats.CacheHits,
            CachedEntries = _cache.Count
        };
    }

    private GeoLocation GenerateMockLocation(string ipAddress)
    {
        // Very simple mock logic - replace with MaxMind in production
        var firstOctet = ipAddress.Split('.').FirstOrDefault();

        return firstOctet switch
        {
            // US ranges (simplified)
            "3" or "13" or "18" or "52" => new GeoLocation
            {
                CountryCode = "US",
                CountryName = "United States",
                ContinentCode = "NA",
                IsHosting = true
            },
            // UK ranges (simplified)
            "51" or "35" => new GeoLocation
            {
                CountryCode = "GB",
                CountryName = "United Kingdom",
                ContinentCode = "EU"
            },
            // China ranges (simplified)
            "1" or "14" or "27" => new GeoLocation
            {
                CountryCode = "CN",
                CountryName = "China",
                ContinentCode = "AS"
            },
            // Default to US
            _ => new GeoLocation
            {
                CountryCode = "US",
                CountryName = "United States",
                ContinentCode = "NA"
            }
        };
    }
}

public class GeoLocationStatistics
{
    public int TotalLookups { get; set; }
    public int CacheHits { get; set; }
    public int CachedEntries { get; set; }
    public bool DatabaseLoaded { get; set; }
    public string? DatabasePath { get; set; }
    public DateTime? LastDatabaseUpdate { get; set; }
}