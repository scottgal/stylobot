using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Services;

/// <summary>
///     Downloads and updates the MaxMind GeoLite2 database on a periodic
///     schedule, then asks <see cref="MaxMindGeoLocationService"/> to hot-reload
///     the reader.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(_options.UpdateCheckInterval)</c> loop; now a plain
///         singleton that subscribes to <see cref="TickCadence.Tick1h"/> on
///         <see cref="IScheduleCoordinator"/>. The handler reuses the existing
///         "is the file older than 7 days?" gate, so the per-hour wake-up is
///         cheap (a single <see cref="File.GetLastWriteTimeUtc"/> call) and the
///         actual MaxMind fetch still happens at most weekly. See
///         <c>feedback_no_background_services</c>.
///     </para>
///     <para>
///         <see cref="IScheduleCoordinator"/> is taken as nullable: a host that
///         hasn't called <c>AddBotDetection</c> won't have a coordinator
///         registered, and the GeoDetection package should not crash that host.
///         When absent, the on-startup download still runs once (via
///         <see cref="EnsureStartupDownloadAsync"/> driven by the hosted
///         bootstrap) but the periodic refresh is skipped.
///     </para>
/// </summary>
public sealed class GeoLite2UpdateService : IDisposable
{
    private const string DownloadBaseUrl = "https://download.maxmind.com/geoip/databases";

    private readonly ILogger<GeoLite2UpdateService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MaxMindGeoLocationService? _geoService;
    private readonly GeoLite2Options _options;
    private readonly IDisposable? _subscription;
    private int _disposed;
    private int _startupRan;
    private DateTimeOffset _lastSuccessfulFetch = DateTimeOffset.MinValue;

    public GeoLite2UpdateService(
        ILogger<GeoLite2UpdateService> logger,
        IOptions<GeoLite2Options> options,
        IHttpClientFactory httpClientFactory,
        IGeoLocationService geoService,
        IScheduleCoordinator? coordinator = null)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _geoService = geoService as MaxMindGeoLocationService;
        _options = options.Value;

        if (!_options.IsAutoDownloadConfigured || !_options.EnableAutoUpdate || coordinator is null)
            return;

        // GeoLite2 updates weekly so the per-hour tick is more than enough.
        // The handler's CheckForUpdateAsync gate keeps the actual MaxMind
        // fetch at most weekly (when the file is >7 days old).
        _subscription = coordinator.Subscribe(
            TickCadence.Tick1h,
            "GeoLite2UpdateService",
            CostHint.High,
            OnTickAsync);
    }

    /// <summary>
    ///     One-shot on-startup download check. The bootstrap shim calls this
    ///     once at host startup; if the database file is missing it kicks off
    ///     the initial fetch synchronously with respect to the bootstrap.
    /// </summary>
    public async Task EnsureStartupDownloadAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _startupRan, 1) != 0) return;

        if (!_options.IsAutoDownloadConfigured)
        {
            _logger.LogInformation(
                "GeoLite2 auto-update disabled. Configure AccountId and LicenseKey in GeoLite2Options " +
                "to enable automatic database downloads. Get a free account at https://www.maxmind.com/en/geolite2/signup");
            return;
        }

        if (!_options.EnableAutoUpdate)
        {
            _logger.LogInformation("GeoLite2 auto-update is disabled in configuration");
            return;
        }

        if (!_options.DownloadOnStartup) return;

        var dbPath = GetDatabasePath();
        if (!File.Exists(dbPath))
        {
            _logger.LogInformation("GeoLite2 database not found, downloading...");
            await DownloadDatabaseAsync(ct);
        }
    }

    /// <summary>
    ///     The coordinator's tick handler. Public so tests can drive a single
    ///     beat deterministically. Reuses the existing CheckForUpdateAsync
    ///     "is file older than 7 days?" gate so the per-hour wake-up doesn't
    ///     hit MaxMind unless an actual update is due.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        try
        {
            await CheckForUpdateAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during GeoLite2 update check");
        }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        var dbPath = GetDatabasePath();
        if (!File.Exists(dbPath))
        {
            _logger.LogInformation("GeoLite2 database missing, downloading...");
            await DownloadDatabaseAsync(cancellationToken);
            return;
        }

        var lastModified = File.GetLastWriteTimeUtc(dbPath);
        var age = DateTime.UtcNow - lastModified;

        // GeoLite2 updates weekly, so check if database is older than 7 days
        if (age > TimeSpan.FromDays(7))
        {
            _logger.LogInformation("GeoLite2 database is {Age:F1} days old, checking for updates...", age.TotalDays);
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
            _logger.LogWarning("Cannot download GeoLite2 database: AccountId and LicenseKey not configured");
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

            _logger.LogInformation("Downloading GeoLite2 database from MaxMind...");

            using var client = _httpClientFactory.CreateClient("GeoLite2");

            // Set up Basic Authentication
            var credentials = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_options.AccountId}:{_options.LicenseKey}"));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);

            var response = await client.GetAsync(downloadUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to download GeoLite2 database: {StatusCode} {Reason}",
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

                _logger.LogDebug("Downloaded {Size} bytes to {Path}",
                    new FileInfo(tarGzPath).Length, tarGzPath);

                // Extract tar.gz
                var extractedMmdb = await ExtractTarGzAsync(tarGzPath, tempDir, $"{dbName}.mmdb", cancellationToken);

                if (extractedMmdb == null)
                {
                    _logger.LogError("Failed to extract .mmdb file from downloaded archive");
                    return false;
                }

                // Move to final location
                var dbPath = GetDatabasePath();
                var dbDir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir)) Directory.CreateDirectory(dbDir);

                // Delete old file if exists
                if (File.Exists(dbPath)) File.Delete(dbPath);

                File.Move(extractedMmdb, dbPath);

                _logger.LogInformation("GeoLite2 database updated successfully at {Path}", dbPath);

                _lastSuccessfulFetch = DateTimeOffset.UtcNow;

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
                    _logger.LogWarning(ex, "Failed to cleanup temp directory {Path}", tempDir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download GeoLite2 database");
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}