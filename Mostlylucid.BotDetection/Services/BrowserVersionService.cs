using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Service for tracking current browser versions.
///     Used by VersionAgeDetector to detect outdated browsers.
/// </summary>
public interface IBrowserVersionService
{
    /// <summary>
    ///     Gets the last successful update time.
    /// </summary>
    DateTime? LastUpdated { get; }

    /// <summary>
    ///     Gets the latest known major version for a browser.
    /// </summary>
    Task<int?> GetLatestVersionAsync(string browserName, CancellationToken ct = default);

    /// <summary>
    ///     Gets all known browser versions.
    /// </summary>
    IReadOnlyDictionary<string, int> GetAllVersions();
}

/// <summary>
///     Background service that periodically fetches current browser versions.
/// </summary>
public partial class BrowserVersionService : BackgroundService, IBrowserVersionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BrowserVersionService> _logger;
    private readonly BotDetectionOptions _options;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);

    public BrowserVersionService(
        ILogger<BrowserVersionService> logger,
        IOptions<BotDetectionOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;

        // Initialize with fallback versions
        foreach (var (browser, version) in _options.VersionAge.FallbackBrowserVersions) _versions[browser] = version;
    }

    public DateTime? LastUpdated { get; private set; }

    public Task<int?> GetLatestVersionAsync(string browserName, CancellationToken ct = default)
    {
        // Normalize browser name
        var normalizedName = NormalizeBrowserName(browserName);

        if (_versions.TryGetValue(normalizedName, out var version)) return Task.FromResult<int?>(version);

        // Try fallback
        if (_options.VersionAge.FallbackBrowserVersions.TryGetValue(normalizedName, out var fallback))
            return Task.FromResult<int?>(fallback);

        return Task.FromResult<int?>(null);
    }

    public IReadOnlyDictionary<string, int> GetAllVersions()
    {
        return new Dictionary<string, int>(_versions);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.VersionAge.Enabled)
        {
            _logger.LogInformation("Version age detection is disabled. Browser version updates skipped.");
            return;
        }

        _logger.LogInformation(
            "Browser version service started. Update interval: {Hours}h",
            _options.VersionAge.UpdateIntervalHours);

        // Initial update (with delay to not slow startup)
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        await UpdateVersionsSafeAsync(stoppingToken);

        // Periodic updates
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                await Task.Delay(
                    TimeSpan.FromHours(_options.VersionAge.UpdateIntervalHours),
                    stoppingToken);

                await UpdateVersionsSafeAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in browser version update loop");
            }

        _logger.LogInformation("Browser version service stopped");
    }

    private async Task UpdateVersionsSafeAsync(CancellationToken ct)
    {
        if (!await _updateLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogDebug("Browser version update already in progress");
            return;
        }

        try
        {
            _logger.LogDebug("Starting browser version update...");

            // Try multiple sources
            var updated = false;

            // Source 1: useragents.me API
            if (_options.DataSources.BrowserVersions.Enabled &&
                !string.IsNullOrEmpty(_options.DataSources.BrowserVersions.Url))
                updated = await TryFetchFromUserAgentsMeAsync(ct);

            // Source 2: Fallback to whatismybrowser.com or other APIs (future)
            // Can add more sources here

            if (updated)
            {
                LastUpdated = DateTime.UtcNow;
                _logger.LogInformation(
                    "Browser versions updated successfully. Versions: {Versions}",
                    string.Join(", ", _versions.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            else
            {
                _logger.LogWarning("Failed to update browser versions from external sources. Using fallback data.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating browser versions");
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<bool> TryFetchFromUserAgentsMeAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("BotDetection");
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetAsync(_options.DataSources.BrowserVersions.Url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Browser version API returned {StatusCode}", response.StatusCode);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);

            // Parse browsers.fyi format: {"chrome": {"version": "143", ...}, "firefox": {...}, ...}
            using var doc = JsonDocument.Parse(json);

            var updated = false;

            // Iterate through all browsers in the response
            foreach (var browserProp in doc.RootElement.EnumerateObject())
            {
                var browserKey = browserProp.Name.ToLowerInvariant();
                var browserData = browserProp.Value;

                // Extract version number
                if (browserData.TryGetProperty("version", out var versionElement))
                {
                    var versionStr = versionElement.GetString();
                    if (!string.IsNullOrEmpty(versionStr))
                    {
                        // Parse major version (e.g., "143.0.6351.0" -> 143)
                        var parts = versionStr.Split('.');
                        if (parts.Length > 0 && int.TryParse(parts[0], out var majorVersion))
                        {
                            // Map browser key to normalized name
                            var browserName = browserKey switch
                            {
                                "chrome" => "Chrome",
                                "firefox" => "Firefox",
                                "safari" => "Safari",
                                "edge" => "Edge",
                                "opera" => "Opera",
                                "brave" => "Brave",
                                "vivaldi" => "Vivaldi",
                                _ => null
                            };

                            if (browserName != null)
                            {
                                UpdateIfNewer(browserName, majorVersion);
                                updated = true;
                            }
                        }
                    }
                }
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching from browser version API");
            return false;
        }
    }

    private void ExtractAndUpdateVersion(string userAgent)
    {
        // Chrome
        var chromeMatch = ChromeVersionRegex().Match(userAgent);
        if (chromeMatch.Success && int.TryParse(chromeMatch.Groups[1].Value, out var chromeVer))
            UpdateIfNewer("Chrome", chromeVer);

        // Edge
        var edgeMatch = EdgeVersionRegex().Match(userAgent);
        if (edgeMatch.Success && int.TryParse(edgeMatch.Groups[1].Value, out var edgeVer))
            UpdateIfNewer("Edge", edgeVer);

        // Firefox
        var firefoxMatch = FirefoxVersionRegex().Match(userAgent);
        if (firefoxMatch.Success && int.TryParse(firefoxMatch.Groups[1].Value, out var ffVer))
            UpdateIfNewer("Firefox", ffVer);

        // Safari
        var safariMatch = SafariVersionRegex().Match(userAgent);
        if (safariMatch.Success && int.TryParse(safariMatch.Groups[1].Value, out var safariVer))
            UpdateIfNewer("Safari", safariVer);

        // Opera
        var operaMatch = OperaVersionRegex().Match(userAgent);
        if (operaMatch.Success && int.TryParse(operaMatch.Groups[1].Value, out var operaVer))
            UpdateIfNewer("Opera", operaVer);
    }

    private void UpdateIfNewer(string browser, int version)
    {
        _versions.AddOrUpdate(
            browser,
            version,
            (_, existing) => Math.Max(existing, version));
    }

    private static string NormalizeBrowserName(string name)
    {
        return name.ToLowerInvariant() switch
        {
            "chrome" or "chromium" => "Chrome",
            "firefox" or "ff" => "Firefox",
            "safari" => "Safari",
            "edge" or "edg" or "msedge" => "Edge",
            "opera" or "opr" => "Opera",
            "brave" => "Brave",
            "vivaldi" => "Vivaldi",
            _ => name
        };
    }

    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeVersionRegex();

    [GeneratedRegex(@"Edg/(\d+)")]
    private static partial Regex EdgeVersionRegex();

    [GeneratedRegex(@"Firefox/(\d+)")]
    private static partial Regex FirefoxVersionRegex();

    [GeneratedRegex(@"Version/(\d+).*Safari")]
    private static partial Regex SafariVersionRegex();

    [GeneratedRegex(@"OPR/(\d+)")]
    private static partial Regex OperaVersionRegex();
}