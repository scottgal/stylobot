using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Data;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Telemetry;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     Wrapper service that adds caching (memory and optionally database) to any geo provider
/// </summary>
public class CachedGeoLocationService : IGeoLocationService
{
    private readonly GeoCacheOptions _cacheOptions;
    private readonly GeoDbContext? _dbContext;
    private readonly IGeoLocationService _innerService;
    private readonly ILogger<CachedGeoLocationService> _logger;
    private readonly IMemoryCache _memoryCache;
    private readonly GeoLite2Options _options;
    private readonly GeoLocationStatistics _stats = new();

    public CachedGeoLocationService(
        IGeoLocationService innerService,
        IMemoryCache memoryCache,
        IOptions<GeoLite2Options> options,
        IOptions<GeoCacheOptions> cacheOptions,
        ILogger<CachedGeoLocationService> logger,
        GeoDbContext? dbContext = null)
    {
        _innerService = innerService;
        _memoryCache = memoryCache;
        _dbContext = dbContext;
        _cacheOptions = cacheOptions.Value;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        using var activity = GeoDetectionTelemetry.StartGetLocationActivity(ipAddress);

        try
        {
            _stats.TotalLookups++;

            // 1. Check memory cache first (fastest)
            var cacheKey = $"geo:{ipAddress}";
            if (_memoryCache.TryGetValue(cacheKey, out GeoLocation? cached))
            {
                _stats.CacheHits++;
                GeoDetectionTelemetry.RecordResult(activity, cached, true);
                GeoDetectionTelemetry.RecordCacheSource(activity, "memory");
                return cached;
            }

            // 2. Check database cache if enabled
            if (_cacheOptions.Enabled && _dbContext != null)
            {
                var dbCached = await GetFromDatabaseCacheAsync(ipAddress, cancellationToken);
                if (dbCached != null)
                {
                    _stats.CacheHits++;
                    // Also cache in memory for faster subsequent access
                    CacheInMemory(cacheKey, dbCached);
                    GeoDetectionTelemetry.RecordResult(activity, dbCached, true);
                    GeoDetectionTelemetry.RecordCacheSource(activity, "database");
                    return dbCached;
                }
            }

            // 3. Look up from provider
            var location = await _innerService.GetLocationAsync(ipAddress, cancellationToken);

            if (location != null)
            {
                // Cache in memory
                CacheInMemory(cacheKey, location);

                // Cache in database if enabled
                if (_cacheOptions.Enabled && _dbContext != null)
                    await SaveToDatabaseCacheAsync(ipAddress, location, cancellationToken);
            }

            GeoDetectionTelemetry.RecordResult(activity, location, false);
            GeoDetectionTelemetry.RecordCacheSource(activity, "provider");
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
        var innerStats = _innerService.GetStatistics();
        return new GeoLocationStatistics
        {
            TotalLookups = _stats.TotalLookups,
            CacheHits = _stats.CacheHits,
            CachedEntries = innerStats.CachedEntries,
            DatabaseLoaded = innerStats.DatabaseLoaded,
            DatabasePath = innerStats.DatabasePath,
            LastDatabaseUpdate = innerStats.LastDatabaseUpdate
        };
    }

    private void CacheInMemory(string cacheKey, GeoLocation location)
    {
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(_options.CacheDuration)
            .SetSize(1);
        _memoryCache.Set(cacheKey, location, cacheOptions);
    }

    private async Task<GeoLocation?> GetFromDatabaseCacheAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (_dbContext == null) return null;

        try
        {
            var cached = await _dbContext.CachedLocations
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.IpAddress == ipAddress && c.ExpiresAt > DateTime.UtcNow, cancellationToken);

            if (cached == null) return null;

            return new GeoLocation
            {
                CountryCode = cached.CountryCode,
                CountryName = cached.CountryName,
                ContinentCode = cached.ContinentCode,
                RegionCode = cached.RegionCode,
                City = cached.City,
                Latitude = cached.Latitude,
                Longitude = cached.Longitude,
                TimeZone = cached.TimeZone,
                IsVpn = cached.IsVpn,
                IsHosting = cached.IsHosting
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read from geo cache database");
            return null;
        }
    }

    private async Task SaveToDatabaseCacheAsync(string ipAddress, GeoLocation location,
        CancellationToken cancellationToken)
    {
        if (_dbContext == null) return;

        try
        {
            var now = DateTime.UtcNow;
            var cached = new CachedGeoLocation
            {
                IpAddress = ipAddress,
                CountryCode = location.CountryCode,
                CountryName = location.CountryName,
                ContinentCode = location.ContinentCode,
                RegionCode = location.RegionCode,
                City = location.City,
                Latitude = location.Latitude,
                Longitude = location.Longitude,
                TimeZone = location.TimeZone,
                IsVpn = location.IsVpn,
                IsHosting = location.IsHosting,
                CachedAt = now,
                ExpiresAt = now.Add(_cacheOptions.CacheExpiration),
                Provider = _options.Provider.ToString()
            };

            var existing = await _dbContext.CachedLocations.FindAsync(new object[] { ipAddress }, cancellationToken);
            if (existing != null)
                _dbContext.Entry(existing).CurrentValues.SetValues(cached);
            else
                _dbContext.CachedLocations.Add(cached);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save to geo cache database");
        }
    }

    /// <summary>
    ///     Clean up expired cache entries
    /// </summary>
    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        if (_dbContext == null) return;

        try
        {
            var now = DateTime.UtcNow;
            var expired = await _dbContext.CachedLocations
                .Where(c => c.ExpiresAt < now)
                .ToListAsync(cancellationToken);

            if (expired.Count > 0)
            {
                _dbContext.CachedLocations.RemoveRange(expired);
                await _dbContext.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleaned up {Count} expired geo cache entries", expired.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup expired geo cache entries");
        }
    }
}