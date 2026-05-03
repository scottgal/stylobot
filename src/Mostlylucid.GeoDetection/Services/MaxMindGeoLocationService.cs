using System.Net;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Telemetry;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     GeoLocation service using MaxMind GeoLite2 local database
/// </summary>
public class MaxMindGeoLocationService : IGeoLocationService, IDisposable
{
    private readonly IMemoryCache _cache;
    private readonly IGeoLocationService? _fallbackService;
    private readonly ILogger<MaxMindGeoLocationService> _logger;
    private readonly GeoLite2Options _options;
    private readonly SemaphoreSlim _readerLock = new(1, 1);
    private readonly GeoLocationStatistics _stats = new();
    private DateTime _lastReaderUpdate = DateTime.MinValue;

    private DatabaseReader? _reader;

    public MaxMindGeoLocationService(
        ILogger<MaxMindGeoLocationService> logger,
        IOptions<GeoLite2Options> options,
        IMemoryCache cache,
        IGeoLocationService? fallbackService = null)
    {
        _logger = logger;
        _options = options.Value;
        _cache = cache;
        _fallbackService = fallbackService;

        InitializeReader();
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _readerLock.Dispose();
    }

    public async Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        using var activity = GeoDetectionTelemetry.StartGetLocationActivity(ipAddress);

        try
        {
            _stats.TotalLookups++;

            // Check cache first
            var cacheKey = $"geo:{ipAddress}";
            if (_cache.TryGetValue(cacheKey, out GeoLocation? cached))
            {
                _stats.CacheHits++;
                GeoDetectionTelemetry.RecordResult(activity, cached, true);
                GeoDetectionTelemetry.RecordCacheSource(activity, "memory");
                return cached;
            }

            // Try MaxMind lookup
            var location = await LookupMaxMindAsync(ipAddress, cancellationToken);

            // Fall back to simple service if configured
            if (location == null && _options.FallbackToSimple && _fallbackService != null)
            {
                _logger.LogDebug("Falling back to simple geo service for {IP}", ipAddress);
                location = await _fallbackService.GetLocationAsync(ipAddress, cancellationToken);
            }

            // Cache the result
            if (location != null)
            {
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_options.CacheDuration)
                    .SetSize(1);
                _cache.Set(cacheKey, location, cacheOptions);
            }

            GeoDetectionTelemetry.RecordResult(activity, location, false);
            return location;
        }
        catch (Exception ex)
        {
            GeoDetectionTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    public async Task<bool> IsFromCountryAsync(string ipAddress, string countryCode,
        CancellationToken cancellationToken = default)
    {
        using var activity = GeoDetectionTelemetry.StartIsFromCountryActivity(ipAddress, countryCode);

        try
        {
            var location = await GetLocationAsync(ipAddress, cancellationToken);
            var isFromCountry = location?.CountryCode.Equals(countryCode, StringComparison.OrdinalIgnoreCase) ?? false;

            GeoDetectionTelemetry.RecordCountryCheckResult(activity, isFromCountry, location?.CountryCode);
            return isFromCountry;
        }
        catch (Exception ex)
        {
            GeoDetectionTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    public GeoLocationStatistics GetStatistics()
    {
        return new GeoLocationStatistics
        {
            TotalLookups = _stats.TotalLookups,
            CacheHits = _stats.CacheHits,
            CachedEntries = _stats.CachedEntries,
            DatabaseLoaded = _reader != null,
            DatabasePath = GetDatabasePath(),
            LastDatabaseUpdate = _lastReaderUpdate
        };
    }

    private void InitializeReader()
    {
        try
        {
            var dbPath = GetDatabasePath();
            if (File.Exists(dbPath))
            {
                _reader = new DatabaseReader(dbPath);
                _lastReaderUpdate = File.GetLastWriteTimeUtc(dbPath);
                _logger.LogInformation("MaxMind GeoLite2 database loaded from {Path}", dbPath);
            }
            else
            {
                _logger.LogWarning("MaxMind GeoLite2 database not found at {Path}. " +
                                   "Configure GeoLite2Options with AccountId and LicenseKey to enable auto-download, " +
                                   "or download manually from https://dev.maxmind.com/geoip/geolite2-free-geolocation-data",
                    dbPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize MaxMind database reader");
        }
    }

    /// <summary>
    ///     Reload the database reader (called after updates)
    /// </summary>
    public async Task ReloadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _readerLock.WaitAsync(cancellationToken);
        try
        {
            _reader?.Dispose();
            _reader = null;
            InitializeReader();
        }
        finally
        {
            _readerLock.Release();
        }
    }

    private Task<GeoLocation?> LookupMaxMindAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (_reader == null)
        {
            _logger.LogDebug("MaxMind reader not available for lookup of {IP}", ipAddress);
            return Task.FromResult<GeoLocation?>(null);
        }

        try
        {
            if (!IPAddress.TryParse(ipAddress, out var ip))
            {
                _logger.LogWarning("Invalid IP address format: {IP}", ipAddress);
                return Task.FromResult<GeoLocation?>(null);
            }

            // Skip private/reserved addresses
            if (IsPrivateOrReserved(ip))
                return Task.FromResult<GeoLocation?>(new GeoLocation
                {
                    CountryCode = "XX",
                    CountryName = "Private Network",
                    ContinentCode = "XX"
                });

            var response = _options.DatabaseType switch
            {
                GeoLite2DatabaseType.City => LookupCity(ip),
                GeoLite2DatabaseType.Country => LookupCountry(ip),
                _ => LookupCity(ip)
            };

            return Task.FromResult(response);
        }
        catch (AddressNotFoundException)
        {
            _logger.LogDebug("IP address not found in database: {IP}", ipAddress);
            return Task.FromResult<GeoLocation?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error looking up IP {IP} in MaxMind database", ipAddress);
            return Task.FromResult<GeoLocation?>(null);
        }
    }

    private GeoLocation? LookupCity(IPAddress ip)
    {
        if (_reader == null) return null;

        if (!_reader.TryCity(ip, out var cityResponse) || cityResponse == null)
            return null;

        return new GeoLocation
        {
            CountryCode = cityResponse.Country.IsoCode ?? "XX",
            CountryName = cityResponse.Country.Name ?? "Unknown",
            ContinentCode = cityResponse.Continent.Code,
            RegionCode = cityResponse.MostSpecificSubdivision?.IsoCode,
            City = cityResponse.City?.Name,
            Latitude = cityResponse.Location?.Latitude,
            Longitude = cityResponse.Location?.Longitude,
            TimeZone = cityResponse.Location?.TimeZone,
            // Note: IsVpn/IsHosting requires GeoIP2 Anonymous IP database (paid)
            IsVpn = false,
            IsHosting = false
        };
    }

    private GeoLocation? LookupCountry(IPAddress ip)
    {
        if (_reader == null) return null;

        if (!_reader.TryCountry(ip, out var countryResponse) || countryResponse == null)
            return null;

        return new GeoLocation
        {
            CountryCode = countryResponse.Country.IsoCode ?? "XX",
            CountryName = countryResponse.Country.Name ?? "Unknown",
            ContinentCode = countryResponse.Continent.Code
        };
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();

        // IPv4 private ranges
        if (bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10)
                return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168)
                return true;
            // 127.0.0.0/8
            if (bytes[0] == 127)
                return true;
            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254)
                return true;
        }

        return false;
    }

    private string GetDatabasePath()
    {
        var path = _options.DatabasePath;
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
        return path;
    }
}