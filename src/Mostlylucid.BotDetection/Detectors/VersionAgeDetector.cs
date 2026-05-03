using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Detectors;

/// <summary>
///     Detects bots by analyzing browser and OS version age.
///     Bots often use outdated browser versions or impossible browser/OS combinations.
/// </summary>
/// <remarks>
///     <para>
///         This detector catches common bot mistakes:
///         <list type="bullet">
///             <item>Using very old browser versions (e.g., Chrome 70 in 2024)</item>
///             <item>Using ancient OS versions (Windows XP, Android 4)</item>
///             <item>Impossible combinations (Chrome 130 on Windows XP - impossible)</item>
///             <item>Hardcoded User-Agents that never update</item>
///         </list>
///     </para>
///     <para>
///         Browser version data is fetched from external APIs and cached.
///         Falls back to configured defaults if APIs are unavailable.
///     </para>
/// </remarks>
public partial class VersionAgeDetector : IDetector
{
    private readonly ILogger<VersionAgeDetector> _logger;
    private readonly VersionAgeOptions _options;
    private readonly IBrowserVersionService _versionService;

    public VersionAgeDetector(
        ILogger<VersionAgeDetector> logger,
        IOptions<BotDetectionOptions> options,
        IBrowserVersionService versionService)
    {
        _logger = logger;
        _options = options.Value.VersionAge;
        _versionService = versionService;
    }

    public string Name => "Version Age Detector";

    /// <summary>Stage 0: Raw signal extraction - no dependencies</summary>
    public DetectorStage Stage => DetectorStage.RawSignals;

    public async Task<DetectorResult> DetectAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new DetectorResult();

        if (!_options.Enabled) return result;

        var userAgent = context.Request.Headers.UserAgent.ToString();

        if (string.IsNullOrWhiteSpace(userAgent)) return result; // Let UserAgentDetector handle missing UA

        try
        {
            var (browserName, browserVersion) = ExtractBrowserVersion(userAgent);
            var (osName, osVersion) = ExtractOsVersion(userAgent);

            // Check browser version age
            if (_options.CheckBrowserVersion && browserName != null && browserVersion.HasValue)
                await CheckBrowserVersionAge(browserName, browserVersion.Value, result, cancellationToken);

            // Check OS version age
            if (_options.CheckOsVersion && osName != null) CheckOsVersionAge(osName, osVersion, result);

            // Check for impossible combinations
            if (browserName != null && browserVersion.HasValue && osName != null)
                CheckImpossibleCombination(browserName, browserVersion.Value, osName, osVersion, userAgent, result);

            // Add combined boost if both browser AND OS are outdated
            if (result.Reasons.Any(r => r.Category == "BrowserVersion") &&
                result.Reasons.Any(r => r.Category == "OsVersion"))
            {
                result.Confidence += _options.CombinedOutdatedBoost;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "VersionAge",
                    Detail = "Both browser AND OS are outdated - suspicious combination",
                    ConfidenceImpact = _options.CombinedOutdatedBoost
                });
            }

            stopwatch.Stop();
            _logger.LogDebug(
                "Version age detection completed in {ElapsedMs}ms. Browser: {Browser} v{BrowserVersion}, OS: {Os}. Confidence: {Confidence:F2}",
                stopwatch.ElapsedMilliseconds, browserName ?? "unknown", browserVersion?.ToString() ?? "?",
                osName ?? "unknown", result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during version age detection for UA: {UserAgent}", userAgent);
        }

        return result;
    }

    // Pre-compiled regex for extracting browser versions from User-Agent
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

    [GeneratedRegex(@"Brave/(\d+)")]
    private static partial Regex BraveVersionRegex();

    // OS extraction patterns
    [GeneratedRegex(@"Windows NT (\d+\.\d+)")]
    private static partial Regex WindowsVersionRegex();

    [GeneratedRegex(@"Mac OS X (\d+[_\.]\d+)")]
    private static partial Regex MacOsVersionRegex();

    [GeneratedRegex(@"Android (\d+)")]
    private static partial Regex AndroidVersionRegex();

    [GeneratedRegex(@"iPhone OS (\d+)|iPad.*OS (\d+)")]
    private static partial Regex IosVersionRegex();

    private async Task CheckBrowserVersionAge(string browserName, int browserVersion, DetectorResult result,
        CancellationToken ct)
    {
        var latestVersion = await _versionService.GetLatestVersionAsync(browserName, ct);

        if (!latestVersion.HasValue)
        {
            _logger.LogWarning(
                "No version data available for browser: {Browser}. Version age check skipped. Check if BrowserVersionService is registered and data source is accessible.",
                browserName);
            return;
        }

        var versionAge = latestVersion.Value - browserVersion;

        _logger.LogDebug(
            "Browser version comparison: {Browser} reported={Version}, latest={Latest}, age={Age}",
            browserName, browserVersion, latestVersion.Value, versionAge);

        if (versionAge > 20)
        {
            // Severely outdated
            result.Confidence += _options.BrowserSeverelyOutdatedConfidence;
            result.Reasons.Add(new DetectionReason
            {
                Category = "BrowserVersion",
                Detail =
                    $"{browserName} v{browserVersion} is {versionAge} versions behind (latest: {latestVersion.Value})",
                ConfidenceImpact = _options.BrowserSeverelyOutdatedConfidence
            });
            _logger.LogDebug("Severely outdated browser: {Browser} v{Version} ({Age} versions behind)",
                browserName, browserVersion, versionAge);
        }
        else if (versionAge > _options.MaxBrowserVersionAge)
        {
            // Moderately outdated
            result.Confidence += _options.BrowserModeratelyOutdatedConfidence;
            result.Reasons.Add(new DetectionReason
            {
                Category = "BrowserVersion",
                Detail =
                    $"{browserName} v{browserVersion} is {versionAge} versions behind (latest: {latestVersion.Value})",
                ConfidenceImpact = _options.BrowserModeratelyOutdatedConfidence
            });
        }
        else if (versionAge > 5)
        {
            // Slightly outdated
            result.Confidence += _options.BrowserSlightlyOutdatedConfidence;
            result.Reasons.Add(new DetectionReason
            {
                Category = "BrowserVersion",
                Detail =
                    $"{browserName} v{browserVersion} is {versionAge} versions behind (latest: {latestVersion.Value})",
                ConfidenceImpact = _options.BrowserSlightlyOutdatedConfidence
            });
        }
    }

    private void CheckOsVersionAge(string osName, string? osVersion, DetectorResult result)
    {
        var osKey = osVersion != null ? $"{osName} {osVersion}" : osName;

        // Find matching OS age classification
        string? ageCategory = null;
        foreach (var (pattern, category) in _options.OsAgeClassification)
            if (osKey.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                ageCategory = category;
                break;
            }

        if (ageCategory == null)
        {
            _logger.LogDebug("No age classification for OS: {Os}", osKey);
            return;
        }

        switch (ageCategory)
        {
            case "ancient":
                result.Confidence += _options.OsAncientConfidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "OsVersion",
                    Detail = $"Ancient OS detected: {osKey} (extremely rare in legitimate traffic)",
                    ConfidenceImpact = _options.OsAncientConfidence
                });
                break;

            case "very_old":
                result.Confidence += _options.OsVeryOldConfidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "OsVersion",
                    Detail = $"Very old OS detected: {osKey}",
                    ConfidenceImpact = _options.OsVeryOldConfidence
                });
                break;

            case "old":
                result.Confidence += _options.OsOldConfidence;
                result.Reasons.Add(new DetectionReason
                {
                    Category = "OsVersion",
                    Detail = $"Old OS detected: {osKey}",
                    ConfidenceImpact = _options.OsOldConfidence
                });
                break;
        }
    }

    private void CheckImpossibleCombination(string browserName, int browserVersion, string osName, string? osVersion,
        string userAgent, DetectorResult result)
    {
        // Check if browser version is too new for the reported OS
        foreach (var (osPattern, maxBrowserVersion) in _options.MinBrowserVersionByOs)
            if (userAgent.Contains(osPattern, StringComparison.OrdinalIgnoreCase))
                // This OS can only run up to maxBrowserVersion of Chrome-family browsers
                if (browserName is "Chrome" or "Edge" or "Brave" or "Opera" && browserVersion > maxBrowserVersion)
                {
                    result.Confidence += _options.ImpossibleCombinationConfidence;
                    result.BotType = BotType.Scraper;
                    result.Reasons.Add(new DetectionReason
                    {
                        Category = "ImpossibleCombination",
                        Detail =
                            $"Impossible: {browserName} v{browserVersion} cannot run on {osPattern} (max supported: v{maxBrowserVersion})",
                        ConfidenceImpact = _options.ImpossibleCombinationConfidence
                    });
                    _logger.LogInformation(
                        "Impossible browser/OS combination detected: {Browser} v{Version} on {Os}",
                        browserName, browserVersion, osPattern);
                    return;
                }
    }

    private static (string? BrowserName, int? Version) ExtractBrowserVersion(string userAgent)
    {
        // Order matters: check more specific patterns first

        // Edge (must check before Chrome as Edge contains "Chrome" in UA)
        var edgeMatch = EdgeVersionRegex().Match(userAgent);
        if (edgeMatch.Success && int.TryParse(edgeMatch.Groups[1].Value, out var edgeVersion))
            return ("Edge", edgeVersion);

        // Opera (must check before Chrome as Opera contains "Chrome" in UA)
        var operaMatch = OperaVersionRegex().Match(userAgent);
        if (operaMatch.Success && int.TryParse(operaMatch.Groups[1].Value, out var operaVersion))
            return ("Opera", operaVersion);

        // Brave
        var braveMatch = BraveVersionRegex().Match(userAgent);
        if (braveMatch.Success && int.TryParse(braveMatch.Groups[1].Value, out var braveVersion))
            return ("Brave", braveVersion);

        // Chrome (generic - includes many Chromium-based browsers)
        var chromeMatch = ChromeVersionRegex().Match(userAgent);
        if (chromeMatch.Success && int.TryParse(chromeMatch.Groups[1].Value, out var chromeVersion))
            return ("Chrome", chromeVersion);

        // Firefox
        var firefoxMatch = FirefoxVersionRegex().Match(userAgent);
        if (firefoxMatch.Success && int.TryParse(firefoxMatch.Groups[1].Value, out var firefoxVersion))
            return ("Firefox", firefoxVersion);

        // Safari (must check after Chrome as Safari UA can be complex)
        var safariMatch = SafariVersionRegex().Match(userAgent);
        if (safariMatch.Success && int.TryParse(safariMatch.Groups[1].Value, out var safariVersion))
            return ("Safari", safariVersion);

        return (null, null);
    }

    private static (string? OsName, string? Version) ExtractOsVersion(string userAgent)
    {
        // Windows
        var windowsMatch = WindowsVersionRegex().Match(userAgent);
        if (windowsMatch.Success)
            return ("Windows NT", windowsMatch.Groups[1].Value);

        // macOS
        var macMatch = MacOsVersionRegex().Match(userAgent);
        if (macMatch.Success)
            return ("Mac OS X", macMatch.Groups[1].Value.Replace('.', '_'));

        // Android
        var androidMatch = AndroidVersionRegex().Match(userAgent);
        if (androidMatch.Success)
            return ("Android", androidMatch.Groups[1].Value);

        // iOS
        var iosMatch = IosVersionRegex().Match(userAgent);
        if (iosMatch.Success)
        {
            var version = iosMatch.Groups[1].Success ? iosMatch.Groups[1].Value : iosMatch.Groups[2].Value;
            return ("iOS", version);
        }

        // Linux (no version)
        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) &&
            !userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
            return ("Linux", null);

        return (null, null);
    }
}