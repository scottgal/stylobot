using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     Background service for downloading and updating GeoLite2 databases
/// </summary>
public class GeoLite2UpdateService(
    ILogger<GeoLite2UpdateService> logger,
    IOptions<GeoLite2Options> options,
    IHttpClientFactory httpClientFactory,
    IGeoLocationService geoService) : BackgroundService
{
    private const string DownloadBaseUrl = "https://download.maxmind.com/geoip/databases";
    private readonly MaxMindGeoLocationService? _geoService = geoService as MaxMindGeoLocationService;
    private readonly GeoLite2Options _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsAutoDownloadConfigured)
        {
            logger.LogInformation(
                "GeoLite2 auto-update disabled. Configure AccountId and LicenseKey in GeoLite2Options " +
                "to enable automatic database downloads. Get a free account at https://www.maxmind.com/en/geolite2/signup");
            return;
        }

        if (!_options.EnableAutoUpdate)
        {
            logger.LogInformation("GeoLite2 auto-update is disabled in configuration");
            return;
        }

        // Download on startup if database doesn't exist
        if (_options.DownloadOnStartup)
        {
            var dbPath = GetDatabasePath();
            if (!File.Exists(dbPath))
            {
                logger.LogInformation("GeoLite2 database not found, downloading...");
                await DownloadDatabaseAsync(stoppingToken);
            }
        }

        // Periodic update check
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await Task.Delay(_options.UpdateCheckInterval, stoppingToken);
                await CheckForUpdateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during GeoLite2 update check");
            }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var dbPath = GetDatabasePath();
        if (!File.Exists(dbPath))
        {
            logger.LogInformation("GeoLite2 database missing, downloading...");
            await DownloadDatabaseAsync(cancellationToken);
            return;
        }

        var lastModified = File.GetLastWriteTimeUtc(dbPath);
        var age = DateTime.UtcNow - lastModified;

        // GeoLite2 updates weekly, so check if database is older than 7 days
        if (age > TimeSpan.FromDays(7))
        {
            logger.LogInformation("GeoLite2 database is {Age:F1} days old, checking for updates...", age.TotalDays);
            await DownloadDatabaseAsync(cancellationToken);
        }
    }

    /// <summary>
    ///     Download or update the GeoLite2 database
    /// </summary>
    public async Task<bool> DownloadDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsAutoDownloadConfigured)
        {
            logger.LogWarning("Cannot download GeoLite2 database: AccountId and LicenseKey not configured");
            return false;
        }

        try
        {
            var dbName = _options.DatabaseType switch
            {
                GeoLite2DatabaseType.City => "GeoLite2-City",
                GeoLite2DatabaseType.Country => "GeoLite2-Country",
                GeoLite2DatabaseType.ASN => "GeoLite2-ASN",
                _ => "GeoLite2-City"
            };

            var downloadUrl = $"{DownloadBaseUrl}/{dbName}/download?suffix=tar.gz";

            logger.LogInformation("Downloading GeoLite2 database from MaxMind...");

            using var client = httpClientFactory.CreateClient("GeoLite2");

            // Set up Basic Authentication
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_options.AccountId}:{_options.LicenseKey}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);

            var response = await client.GetAsync(downloadUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to download GeoLite2 database: {StatusCode} {Reason}",
                    response.StatusCode, response.ReasonPhrase);
                return false;
            }

            // Create temp directory for extraction
            var tempDir = Path.Combine(Path.GetTempPath(), $"geolite2_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var tarGzPath = Path.Combine(tempDir, $"{dbName}.tar.gz");

                // Download to temp file
                await using (var fileStream = File.Create(tarGzPath))
                {
                    await response.Content.CopyToAsync(fileStream, cancellationToken);
                }

                logger.LogDebug("Downloaded {Size} bytes to {Path}",
                    new FileInfo(tarGzPath).Length, tarGzPath);

                // Extract tar.gz
                var extractedMmdb = await ExtractTarGzAsync(tarGzPath, tempDir, $"{dbName}.mmdb", cancellationToken);

                if (extractedMmdb == null)
                {
                    logger.LogError("Failed to extract .mmdb file from downloaded archive");
                    return false;
                }

                // Move to final location
                var dbPath = GetDatabasePath();
                var dbDir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir)) Directory.CreateDirectory(dbDir);

                // Delete old file if exists
                if (File.Exists(dbPath)) File.Delete(dbPath);

                File.Move(extractedMmdb, dbPath);

                logger.LogInformation("GeoLite2 database updated successfully at {Path}", dbPath);

                // Reload the database reader
                if (_geoService != null) await _geoService.ReloadDatabaseAsync(cancellationToken);

                return true;
            }
            finally
            {
                // Cleanup temp directory
                try
                {
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cleanup temp directory {Path}", tempDir);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download GeoLite2 database");
            return false;
        }
    }

    private async Task<string?> ExtractTarGzAsync(string tarGzPath, string extractDir, string targetFileName,
        CancellationToken cancellationToken)
    {
        // First, decompress gzip
        var tarPath = Path.Combine(extractDir, "archive.tar");

        await using (var gzStream = File.OpenRead(tarGzPath))
        await using (var decompressed = new GZipStream(gzStream, CompressionMode.Decompress))
        await using (var tarFile = File.Create(tarPath))
        {
            await decompressed.CopyToAsync(tarFile, cancellationToken);
        }

        // Simple tar extraction - find the .mmdb file
        // TAR format: 512-byte header blocks followed by file content
        await using var tarStream = File.OpenRead(tarPath);
        var header = new byte[512];

        while (true)
        {
            var bytesRead = await tarStream.ReadAsync(header, 0, 512, cancellationToken);
            if (bytesRead < 512) break;

            // Check for end of archive (two zero blocks)
            if (header.All(b => b == 0)) break;

            // Extract filename from header (bytes 0-99)
            var nameBytes = header.Take(100).TakeWhile(b => b != 0).ToArray();
            var name = Encoding.ASCII.GetString(nameBytes);

            // Extract file size from header (bytes 124-135, octal)
            var sizeBytes = header.Skip(124).Take(11).TakeWhile(b => b != 0 && b != ' ').ToArray();
            var sizeStr = Encoding.ASCII.GetString(sizeBytes).Trim();
            var size = string.IsNullOrEmpty(sizeStr) ? 0 : Convert.ToInt64(sizeStr, 8);

            // Check if this is the file we want
            if (name.EndsWith(targetFileName, StringComparison.OrdinalIgnoreCase))
            {
                var outputPath = Path.Combine(extractDir, targetFileName);
                await using var outputFile = File.Create(outputPath);

                var remaining = size;
                var buffer = new byte[8192];
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await tarStream.ReadAsync(buffer, 0, toRead, cancellationToken);
                    if (read == 0) break;
                    await outputFile.WriteAsync(buffer, 0, read, cancellationToken);
                    remaining -= read;
                }

                return outputPath;
            }

            // Skip file content (aligned to 512-byte blocks)
            var skipBytes = size + (512 - size % 512) % 512;
            tarStream.Seek(skipBytes, SeekOrigin.Current);
        }

        return null;
    }

    private string GetDatabasePath()
    {
        var path = _options.DatabasePath;
        if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
        return path;
    }
}