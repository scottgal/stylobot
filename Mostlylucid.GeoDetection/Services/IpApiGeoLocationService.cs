using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     GeoLocation service using ip-api.com free API (no account required)
///     Rate limit: 45 requests/minute for free tier
/// </summary>
public class IpApiGeoLocationService(
    ILogger<IpApiGeoLocationService> logger,
    IOptions<GeoLite2Options> options,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache) : IGeoLocationService
{
    private const string BaseUrl = "http://ip-api.com/json";
    private readonly GeoLite2Options _options = options.Value;
    private readonly SemaphoreSlim _rateLimiter = new(45, 45); // 45 requests per minute
    private readonly GeoLocationStatistics _stats = new();
    private bool _rateLimiterStarted;

    public async Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        EnsureRateLimiterStarted();
        _stats.TotalLookups++;

        // Check cache first
        var cacheKey = $"ipapi:{ipAddress}";
        if (cache.TryGetValue(cacheKey, out GeoLocation? cached))
        {
            _stats.CacheHits++;
            return cached;
        }

        // Skip private IPs
        if (IPAddress.TryParse(ipAddress, out var ip) && IsPrivateOrReserved(ip))
            return new GeoLocation
            {
                CountryCode = "XX",
                CountryName = "Private Network",
                ContinentCode = "XX"
            };

        // Wait for rate limit permit
        if (!await _rateLimiter.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
        {
            logger.LogWarning("ip-api.com rate limit reached, request dropped");
            return null;
        }

        try
        {
            using var client = httpClientFactory.CreateClient("IpApi");
            var url =
                $"{BaseUrl}/{ipAddress}?fields=status,message,continent,continentCode,country,countryCode,region,regionName,city,lat,lon,timezone,hosting,proxy";

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ip-api.com returned {StatusCode} for {IP}", response.StatusCode, ipAddress);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<IpApiResponse>(json);

            if (result == null || result.Status != "success")
            {
                logger.LogWarning("ip-api.com lookup failed for {IP}: {Message}", ipAddress, result?.Message);
                return null;
            }

            var location = new GeoLocation
            {
                CountryCode = result.CountryCode ?? "XX",
                CountryName = result.Country ?? "Unknown",
                ContinentCode = result.ContinentCode,
                RegionCode = result.Region,
                City = result.City,
                Latitude = result.Lat,
                Longitude = result.Lon,
                TimeZone = result.Timezone,
                IsVpn = result.Proxy,
                IsHosting = result.Hosting
            };

            // Cache the result
            var memoryCacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_options.CacheDuration)
                .SetSize(1);
            cache.Set(cacheKey, location, memoryCacheOptions);

            return location;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error looking up {IP} via ip-api.com", ipAddress);
            return null;
        }
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
            CachedEntries = _stats.CachedEntries,
            DatabaseLoaded = true,
            DatabasePath = "ip-api.com (online)"
        };
    }

    private void EnsureRateLimiterStarted()
    {
        if (_rateLimiterStarted) return;
        _rateLimiterStarted = true;
        // Start rate limiter replenishment
        _ = ReplenishRateLimitAsync();
    }

    private async Task ReplenishRateLimitAsync()
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            // Release permits back to 45
            while (_rateLimiter.CurrentCount < 45) _rateLimiter.Release();
        }
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return false;

        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168) ||
               bytes[0] == 127 ||
               (bytes[0] == 169 && bytes[1] == 254);
    }

    private class IpApiResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }

        [JsonPropertyName("message")] public string? Message { get; set; }

        [JsonPropertyName("continent")] public string? Continent { get; set; }

        [JsonPropertyName("continentCode")] public string? ContinentCode { get; set; }

        [JsonPropertyName("country")] public string? Country { get; set; }

        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }

        [JsonPropertyName("region")] public string? Region { get; set; }

        [JsonPropertyName("regionName")] public string? RegionName { get; set; }

        [JsonPropertyName("city")] public string? City { get; set; }

        [JsonPropertyName("lat")] public double? Lat { get; set; }

        [JsonPropertyName("lon")] public double? Lon { get; set; }

        [JsonPropertyName("timezone")] public string? Timezone { get; set; }

        [JsonPropertyName("proxy")] public bool Proxy { get; set; }

        [JsonPropertyName("hosting")] public bool Hosting { get; set; }
    }
}