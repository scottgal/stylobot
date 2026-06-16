using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Scrapes and caches most common user agents from useragents.me
///     Used for browser version extraction and anomaly detection.
///     Extends IBrowserVersionService to provide backward compatibility.
/// </summary>
public interface ICommonUserAgentService : IBrowserVersionService
{
    /// <summary>
    ///     Gets the most common user agents with prevalence percentages.
    /// </summary>
    IReadOnlyList<CommonUserAgent> GetCommonUserAgents(UserAgentPlatform platform);
}

/// <summary>
///     User agent platform type.
/// </summary>
public enum UserAgentPlatform
{
    Desktop,
    Mobile
}

/// <summary>
///     Represents a common user agent with its prevalence.
/// </summary>
public record CommonUserAgent
{
    public required string UserAgent { get; init; }
    public required double Percentage { get; init; }
}

/// <summary>
///     Service that periodically scrapes common user agents from useragents.me.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(UpdateIntervalHours)</c> loop; now subscribes to
///         <see cref="TickCadence.Tick1h"/> on
///         <see cref="IScheduleCoordinator"/> and gates the fetch on
///         "last-success older than <see cref="VersionAgeOptions.UpdateIntervalHours"/>".
///         The in-memory dictionaries are unchanged -- per the Wave 2 plan,
///         cold-start fallback values cover the loss-on-restart window. See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed partial class CommonUserAgentService : ICommonUserAgentService, IDisposable
{
    private readonly ConcurrentDictionary<string, int> _browserVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CommonUserAgentService> _logger;
    private readonly BotDetectionOptions _options;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly ConcurrentDictionary<UserAgentPlatform, List<CommonUserAgent>> _userAgents = new();
    private readonly IDisposable _subscription;
    private int _disposed;
    private DateTime? _lastSuccessfulFetchUtc;

    public CommonUserAgentService(
        ILogger<CommonUserAgentService> logger,
        IOptions<BotDetectionOptions> options,
        IHttpClientFactory httpClientFactory,
        IScheduleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _logger = logger;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;

        // Initialize with empty lists
        _userAgents[UserAgentPlatform.Desktop] = new List<CommonUserAgent>();
        _userAgents[UserAgentPlatform.Mobile] = new List<CommonUserAgent>();

        // Initialize with fallback versions
        foreach (var (browser, version) in _options.VersionAge.FallbackBrowserVersions)
            _browserVersions[browser] = version;

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1h,
            "CommonUserAgentService",
            CostHint.Medium,
            OnTickAsync);
    }

    public DateTime? LastUpdated { get; private set; }

    public IReadOnlyList<CommonUserAgent> GetCommonUserAgents(UserAgentPlatform platform)
    {
        return _userAgents.TryGetValue(platform, out var list) ? list : new List<CommonUserAgent>();
    }

    public IReadOnlyDictionary<string, int> GetAllVersions()
    {
        return new Dictionary<string, int>(_browserVersions);
    }

    public Task<int?> GetLatestVersionAsync(string browserName, CancellationToken ct = default)
    {
        var normalizedName = NormalizeBrowserName(browserName);

        if (_browserVersions.TryGetValue(normalizedName, out var version)) return Task.FromResult<int?>(version);

        // Try fallback
        if (_options.VersionAge.FallbackBrowserVersions.TryGetValue(normalizedName, out var fallback))
            return Task.FromResult<int?>(fallback);

        return Task.FromResult<int?>(null);
    }

    // Regex patterns for browser version extraction
    [GeneratedRegex(@"Chrome/(\d+)")]
    private static partial Regex ChromeVersionRegex();

    [GeneratedRegex(@"Firefox/(\d+)")]
    private static partial Regex FirefoxVersionRegex();

    [GeneratedRegex(@"Version/(\d+).*Safari")]
    private static partial Regex SafariVersionRegex();

    [GeneratedRegex(@"Edg/(\d+)")]
    private static partial Regex EdgeVersionRegex();

    [GeneratedRegex(@"OPR/(\d+)")]
    private static partial Regex OperaVersionRegex();

    /// <summary>
    ///     The coordinator's tick handler. Fires every Tick1h; gates the
    ///     remote scrape on "last-success older than configured update
    ///     interval" so the actual scraping cadence honours
    ///     <see cref="VersionAgeOptions.UpdateIntervalHours"/>. Public so
    ///     tests can drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (!_options.VersionAge.Enabled) return;
        if (!_options.DataSources.BrowserVersions.Enabled) return;

        var intervalHours = _options.VersionAge.UpdateIntervalHours;
        if (intervalHours <= 0) return;

        var lastSuccess = _lastSuccessfulFetchUtc;
        if (lastSuccess != null && now.UtcDateTime - lastSuccess.Value < TimeSpan.FromHours(intervalHours))
            return; // Not yet due.

        await UpdateUserAgentsSafeAsync(ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
        _updateLock.Dispose();
    }

    private async Task UpdateUserAgentsSafeAsync(CancellationToken ct)
    {
        if (!await _updateLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogDebug("Common user agent update already in progress");
            return;
        }

        try
        {
            _logger.LogDebug("Starting common user agent update...");

            var updated = await ScrapeUserAgentsFromWebsite(ct);

            if (updated)
            {
                LastUpdated = DateTime.UtcNow;
                _lastSuccessfulFetchUtc = DateTime.UtcNow;
                _logger.LogInformation(
                    "Common user agents updated successfully. Desktop: {DesktopCount}, Mobile: {MobileCount}, Browser versions: {Versions}",
                    _userAgents[UserAgentPlatform.Desktop].Count,
                    _userAgents[UserAgentPlatform.Mobile].Count,
                    string.Join(", ", _browserVersions.Select(kv => $"{kv.Key}={kv.Value}")));
            }
            else
            {
                _logger.LogWarning("Failed to update common user agents from useragents.me. Using fallback data.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating common user agents");
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private async Task<bool> ScrapeUserAgentsFromWebsite(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("BotDetection");
            client.Timeout = TimeSpan.FromSeconds(30);

            var response = await client.GetAsync("https://www.useragents.me/", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("useragents.me returned {StatusCode}", response.StatusCode);
                return false;
            }

            var html = await response.Content.ReadAsStringAsync(ct);

            // Parse HTML using AngleSharp
            var context = BrowsingContext.New(Configuration.Default);
            var parser = context.GetService<IHtmlParser>();
            if (parser == null)
            {
                _logger.LogError("Failed to get HTML parser");
                return false;
            }

            var document = await parser.ParseDocumentAsync(html, ct);

            // Extract desktop user agents
            var desktopSelector = "#most-common-desktop-useragents-json-csv > div:nth-child(1) > textarea";
            var desktopTextarea = document.QuerySelector(desktopSelector);
            if (desktopTextarea != null)
            {
                var desktopJson = desktopTextarea.TextContent;
                var desktopUAs = ParseUserAgentJson(desktopJson);
                if (desktopUAs.Count > 0)
                {
                    _userAgents[UserAgentPlatform.Desktop] = desktopUAs;
                    ExtractBrowserVersions(desktopUAs);
                }
            }

            // Extract mobile user agents
            var mobileSelector = "#most-common-mobile-useragents-json-csv > div:nth-child(1) > textarea";
            var mobileTextarea = document.QuerySelector(mobileSelector);
            if (mobileTextarea != null)
            {
                var mobileJson = mobileTextarea.TextContent;
                var mobileUAs = ParseUserAgentJson(mobileJson);
                if (mobileUAs.Count > 0)
                {
                    _userAgents[UserAgentPlatform.Mobile] = mobileUAs;
                    ExtractBrowserVersions(mobileUAs);
                }
            }

            return _userAgents[UserAgentPlatform.Desktop].Count > 0 ||
                   _userAgents[UserAgentPlatform.Mobile].Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scraping user agents from useragents.me");
            return false;
        }
    }

    private List<CommonUserAgent> ParseUserAgentJson(string json)
    {
        try
        {
            // Parse JSON array of {"ua": "...", "pct": 43.03}
            var options = new JsonSerializerOptions
            {
                TypeInfoResolver = BotDetectionJsonSerializerContext.Default
            };

            var items = JsonSerializer.Deserialize(json, BotDetectionJsonSerializerContext.Default.ListJsonElement);
            if (items == null) return new List<CommonUserAgent>();

            var result = new List<CommonUserAgent>();
            foreach (var item in items)
                if (item.TryGetProperty("ua", out var uaElement) &&
                    item.TryGetProperty("pct", out var pctElement))
                {
                    var ua = uaElement.GetString();
                    var pct = pctElement.GetDouble();

                    if (!string.IsNullOrEmpty(ua))
                        result.Add(new CommonUserAgent
                        {
                            UserAgent = ua,
                            Percentage = pct
                        });
                }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing user agent JSON");
            return new List<CommonUserAgent>();
        }
    }

    private void ExtractBrowserVersions(List<CommonUserAgent> userAgents)
    {
        foreach (var ua in userAgents)
        {
            // Extract versions from UA strings and update if newer
            var chromeMatch = ChromeVersionRegex().Match(ua.UserAgent);
            if (chromeMatch.Success && int.TryParse(chromeMatch.Groups[1].Value, out var chromeVer))
                UpdateIfNewer("Chrome", chromeVer);

            var edgeMatch = EdgeVersionRegex().Match(ua.UserAgent);
            if (edgeMatch.Success && int.TryParse(edgeMatch.Groups[1].Value, out var edgeVer))
                UpdateIfNewer("Edge", edgeVer);

            var firefoxMatch = FirefoxVersionRegex().Match(ua.UserAgent);
            if (firefoxMatch.Success && int.TryParse(firefoxMatch.Groups[1].Value, out var ffVer))
                UpdateIfNewer("Firefox", ffVer);

            var safariMatch = SafariVersionRegex().Match(ua.UserAgent);
            if (safariMatch.Success && int.TryParse(safariMatch.Groups[1].Value, out var safariVer))
                UpdateIfNewer("Safari", safariVer);

            var operaMatch = OperaVersionRegex().Match(ua.UserAgent);
            if (operaMatch.Success && int.TryParse(operaMatch.Groups[1].Value, out var operaVer))
                UpdateIfNewer("Opera", operaVer);
        }
    }

    private void UpdateIfNewer(string browser, int version)
    {
        _browserVersions.AddOrUpdate(
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
}
