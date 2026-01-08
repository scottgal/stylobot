using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     GeoLocation service using free DataHub GeoIP2-IPv4 CSV database
///     No account required! Data from: https://datahub.io/core/geoip2-ipv4
/// </summary>
public class DataHubGeoLocationService(
    ILogger<DataHubGeoLocationService> logger,
    IOptions<GeoLite2Options> options,
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache) : IGeoLocationService, IHostedService
{
    private const string CsvUrl = "https://datahub.io/core/geoip2-ipv4/r/geoip2-ipv4.csv";
    private const string DefaultCsvPath = "data/geoip2-ipv4.csv";
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly GeoLite2Options _options = options.Value;
    private readonly GeoLocationStatistics _stats = new();

    private List<IpRange>? _ipRanges;
    private DateTime _lastUpdate = DateTime.MinValue;

    public async Task<GeoLocation?> GetLocationAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        _stats.TotalLookups++;

        // Check cache first
        var cacheKey = $"datahub:{ipAddress}";
        if (cache.TryGetValue(cacheKey, out GeoLocation? cached))
        {
            _stats.CacheHits++;
            return cached;
        }

        // Ensure database is loaded
        await EnsureDatabaseLoadedAsync(cancellationToken);

        if (_ipRanges == null || _ipRanges.Count == 0)
        {
            logger.LogWarning("DataHub database not loaded");
            return null;
        }

        // Parse IP
        if (!IPAddress.TryParse(ipAddress, out var ip)) return null;

        // Skip private IPs
        if (IsPrivateOrReserved(ip))
            return new GeoLocation
            {
                CountryCode = "XX",
                CountryName = "Private Network",
                ContinentCode = "XX"
            };

        // Convert to uint for comparison (IPv4 only)
        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4) return null; // IPv6 not supported in this dataset

        var ipUint = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        // Binary search for matching range
        var location = FindLocation(ipUint);

        // Cache the result
        if (location != null)
        {
            var memoryCacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(_options.CacheDuration)
                .SetSize(1);
            cache.Set(cacheKey, location, memoryCacheOptions);
        }

        return location;
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
            CachedEntries = _ipRanges?.Count ?? 0,
            DatabaseLoaded = _ipRanges != null && _ipRanges.Count > 0,
            DatabasePath = GetCsvPath(),
            LastDatabaseUpdate = _lastUpdate
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Load database on startup
        await EnsureDatabaseLoadedAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private GeoLocation? FindLocation(uint ip)
    {
        if (_ipRanges == null) return null;

        // Binary search
        int left = 0, right = _ipRanges.Count - 1;

        while (left <= right)
        {
            var mid = (left + right) / 2;
            var range = _ipRanges[mid];

            if (ip < range.StartIp)
                right = mid - 1;
            else if (ip > range.EndIp)
                left = mid + 1;
            else
                // Found!
                return new GeoLocation
                {
                    CountryCode = range.CountryCode,
                    CountryName = range.CountryName,
                    ContinentCode = range.ContinentCode
                };
        }

        return null;
    }

    private async Task EnsureDatabaseLoadedAsync(CancellationToken cancellationToken)
    {
        if (_ipRanges != null && _ipRanges.Count > 0)
            return;

        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (_ipRanges != null && _ipRanges.Count > 0)
                return;

            var csvPath = GetCsvPath();

            // Check if we need to download
            if (!File.Exists(csvPath) || File.GetLastWriteTimeUtc(csvPath) < DateTime.UtcNow.AddDays(-7))
                await DownloadDatabaseAsync(csvPath, cancellationToken);

            // Load from file
            if (File.Exists(csvPath)) await LoadFromCsvAsync(csvPath, cancellationToken);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task DownloadDatabaseAsync(string csvPath, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Downloading GeoIP database from DataHub...");

            using var client = httpClientFactory.CreateClient("DataHub");
            var response = await client.GetAsync(CsvUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to download DataHub database: {Status}", response.StatusCode);
                return;
            }

            var dir = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            await using var fileStream = File.Create(csvPath);
            await response.Content.CopyToAsync(fileStream, cancellationToken);

            logger.LogInformation("Downloaded DataHub GeoIP database to {Path}", csvPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download DataHub database");
        }
    }

    private async Task LoadFromCsvAsync(string csvPath, CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Loading GeoIP database from {Path}...", csvPath);

            var ranges = new List<IpRange>();
            var lines = await File.ReadAllLinesAsync(csvPath, cancellationToken);

            // Skip header
            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = ParseCsvLine(line);
                if (parts.Length < 6) continue;

                // CSV format: network,geoname_id,continent_code,continent_name,country_iso_code,country_name
                var network = parts[0];
                var continentCode = parts[2];
                var continentName = parts[3];
                var countryCode = parts[4];
                var countryName = parts[5];

                if (string.IsNullOrEmpty(countryCode)) continue;

                // Parse CIDR
                var cidrParts = network.Split('/');
                if (cidrParts.Length != 2) continue;

                if (!IPAddress.TryParse(cidrParts[0], out var baseIp)) continue;
                if (!int.TryParse(cidrParts[1], out var prefixLength)) continue;

                var bytes = baseIp.GetAddressBytes();
                if (bytes.Length != 4) continue; // IPv4 only

                var baseUint = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
                var mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
                var startIp = baseUint & mask;
                var endIp = startIp | ~mask;

                ranges.Add(new IpRange
                {
                    StartIp = startIp,
                    EndIp = endIp,
                    CountryCode = countryCode,
                    CountryName = countryName,
                    ContinentCode = continentCode
                });
            }

            // Sort by start IP for binary search
            ranges.Sort((a, b) => a.StartIp.CompareTo(b.StartIp));

            _ipRanges = ranges;
            _lastUpdate = File.GetLastWriteTimeUtc(csvPath);

            logger.LogInformation("Loaded {Count} IP ranges from DataHub database", ranges.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load DataHub database");
        }
    }

    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = "";
        var inQuotes = false;

        foreach (var c in line)
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }

        result.Add(current);

        return result.ToArray();
    }

    private string GetCsvPath()
    {
        var path = _options.DatabasePath;
        if (path.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            // Change extension for CSV
            path = Path.ChangeExtension(path, ".csv");

        if (!path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) path = DefaultCsvPath;

        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
        return path;
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

    private class IpRange
    {
        public uint StartIp { get; set; }
        public uint EndIp { get; set; }
        public string CountryCode { get; set; } = "";
        public string CountryName { get; set; } = "";
        public string ContinentCode { get; set; } = "";
    }
}