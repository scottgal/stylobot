using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Service for fetching and caching bot detection lists from authoritative sources.
///     All data source URLs are configurable via BotDetectionOptions.DataSources.
/// </summary>
public interface IBotListFetcher
{
    /// <summary>
    ///     Fetches bot patterns from all enabled sources (IsBot, Matomo, crawler-user-agents).
    ///     Returns regex patterns for user-agent matching.
    /// </summary>
    Task<List<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fetches datacenter IP ranges from all enabled sources (AWS, GCP, Azure, Cloudflare).
    ///     Returns CIDR notation IP ranges.
    /// </summary>
    Task<List<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fetches Matomo bot patterns with metadata (name, category, url).
    ///     Only fetches if Matomo source is enabled in configuration.
    /// </summary>
    Task<List<BotPattern>> GetMatomoBotPatternsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fetches security tool patterns from configured sources (digininja, OWASP CoreRuleSet).
    ///     Returns patterns for identifying penetration testing and vulnerability scanning tools.
    ///     Part of the security detection layer for API honeypot integration.
    /// </summary>
    Task<List<SecurityToolPattern>> GetSecurityToolPatternsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Represents a bot pattern with metadata from Matomo Device Detector.
/// </summary>
public class BotPattern
{
    public string? Name { get; set; }
    public string? Pattern { get; set; }
    public string? Category { get; set; }
    public string? Url { get; set; }
}

/// <summary>
///     Represents a security tool pattern with metadata.
///     Used for detecting penetration testing tools, vulnerability scanners, and exploit frameworks.
/// </summary>
public class SecurityToolPattern
{
    /// <summary>The pattern to match (substring or regex)</summary>
    public string Pattern { get; set; } = "";

    /// <summary>Name of the security tool (e.g., "SQLMap", "Nikto")</summary>
    public string? Name { get; set; }

    /// <summary>Category of tool (e.g., "SqlInjection", "VulnerabilityScanner")</summary>
    public string? Category { get; set; }

    /// <summary>Whether this is a regex pattern (vs simple substring)</summary>
    public bool IsRegex { get; set; }

    /// <summary>Source where this pattern was obtained</summary>
    public string? Source { get; set; }
}

/// <summary>
///     Fetches bot lists from configurable authoritative sources with caching.
///     Fail-safe design: failures are logged but never crash the app.
/// </summary>
public partial class BotListFetcher : IBotListFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Use source generator context for AOT/NativeAOT support
        TypeInfoResolver = BotDetectionJsonSerializerContext.Default
    };

    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BotListFetcher> _logger;
    private readonly BotDetectionOptions _options;

    public BotListFetcher(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<BotListFetcher> logger,
        IOptions<BotDetectionOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
        _options = options.Value;
    }

#pragma warning disable CS0618 // Type or member is obsolete
    private TimeSpan CacheDuration => TimeSpan.FromHours(_options.UpdateIntervalHours);
#pragma warning restore CS0618 // Type or member is obsolete

    /// <summary>
    ///     Fetches bot patterns from all enabled sources.
    ///     Sources are fetched based on configuration in BotDetectionOptions.DataSources.
    /// </summary>
    public async Task<List<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default)
    {
        const string cacheKey = "bot_list_all_patterns";

        if (_cache.TryGetValue<List<string>>(cacheKey, out var cached) && cached != null) return cached;

        var allPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = _options.DataSources;
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_options.ListDownloadTimeoutSeconds);

        // Fetch IsBot patterns (primary source - most comprehensive)
        if (sources.IsBot.Enabled && !string.IsNullOrEmpty(sources.IsBot.Url))
            try
            {
                var json = await client.GetStringAsync(sources.IsBot.Url, cancellationToken);
                var patterns = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
                if (patterns != null)
                {
                    var validCount = 0;
                    var invalidCount = 0;
                    foreach (var p in patterns.Where(p => !string.IsNullOrEmpty(p)))
                        if (IsValidPattern(p))
                        {
                            allPatterns.Add(p);
                            validCount++;
                        }
                        else
                        {
                            invalidCount++;
                        }

                    _logger.LogInformation(
                        "Fetched {ValidCount} valid IsBot patterns from {Url} ({InvalidCount} rejected)",
                        validCount, sources.IsBot.Url, invalidCount);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in IsBot patterns from {Url}", sources.IsBot.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch IsBot patterns from {Url}", sources.IsBot.Url);
            }

        // Fetch crawler-user-agents patterns (if enabled - overlaps with isbot)
        if (sources.CrawlerUserAgents.Enabled && !string.IsNullOrEmpty(sources.CrawlerUserAgents.Url))
            try
            {
                var json = await client.GetStringAsync(sources.CrawlerUserAgents.Url, cancellationToken);
                var crawlers = JsonSerializer.Deserialize<List<CrawlerEntry>>(json, JsonOptions);
                if (crawlers != null)
                {
                    var validCount = 0;
                    var invalidCount = 0;
                    foreach (var c in crawlers.Where(c => !string.IsNullOrEmpty(c.Pattern)))
                        if (IsValidPattern(c.Pattern!))
                        {
                            allPatterns.Add(c.Pattern!);
                            validCount++;
                        }
                        else
                        {
                            invalidCount++;
                        }

                    _logger.LogInformation(
                        "Fetched {ValidCount} valid crawler-user-agents patterns from {Url} ({InvalidCount} rejected)",
                        validCount, sources.CrawlerUserAgents.Url, invalidCount);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in crawler-user-agents from {Url}", sources.CrawlerUserAgents.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch crawler-user-agents from {Url}", sources.CrawlerUserAgents.Url);
            }

        // Fetch Matomo patterns as strings (if enabled - overlaps with isbot)
        if (sources.Matomo.Enabled && !string.IsNullOrEmpty(sources.Matomo.Url))
            try
            {
                var yaml = await client.GetStringAsync(sources.Matomo.Url, cancellationToken);
                var matomoPatterns = ParseMatomoYaml(yaml);
                var validCount = 0;
                var invalidCount = 0;
                foreach (var p in matomoPatterns.Where(p => !string.IsNullOrEmpty(p.Pattern)))
                    if (IsValidPattern(p.Pattern!))
                    {
                        allPatterns.Add(p.Pattern!);
                        validCount++;
                    }
                    else
                    {
                        invalidCount++;
                    }

                _logger.LogInformation(
                    "Fetched {ValidCount} valid Matomo patterns from {Url} ({InvalidCount} rejected)",
                    validCount, sources.Matomo.Url, invalidCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Matomo patterns from {Url}", sources.Matomo.Url);
            }

        var result = allPatterns.ToList();

        if (result.Count == 0)
        {
            _logger.LogWarning("No patterns fetched from any source, using fallback patterns");
            result = GetFallbackCrawlerPatterns();
        }
        else
        {
            _logger.LogInformation("Total unique bot patterns: {Count}", result.Count);
        }

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    /// <summary>
    ///     Fetches datacenter IP ranges from all enabled sources.
    ///     Sources are fetched based on configuration in BotDetectionOptions.DataSources.
    /// </summary>
    public async Task<List<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default)
    {
        const string cacheKey = "bot_list_datacenter_ips";

        if (_cache.TryGetValue<List<string>>(cacheKey, out var cached) && cached != null) return cached;

        var ranges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = _options.DataSources;
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_options.ListDownloadTimeoutSeconds);

        // Fetch AWS IP ranges
        if (sources.AwsIpRanges.Enabled && !string.IsNullOrEmpty(sources.AwsIpRanges.Url))
            try
            {
                var awsJson = await client.GetStringAsync(sources.AwsIpRanges.Url, cancellationToken);
                var awsData = JsonSerializer.Deserialize<AwsIpRangesResponse>(awsJson, JsonOptions);
                if (awsData?.Prefixes != null)
                {
                    var validCount = 0;
                    var invalidCount = 0;
                    foreach (var p in awsData.Prefixes.Where(p => !string.IsNullOrEmpty(p.IpPrefix)))
                        if (IsValidCidr(p.IpPrefix!))
                        {
                            ranges.Add(p.IpPrefix!);
                            validCount++;
                        }
                        else
                        {
                            invalidCount++;
                        }

                    _logger.LogInformation(
                        "Fetched {ValidCount} valid AWS IP ranges from {Url} ({InvalidCount} rejected)",
                        validCount, sources.AwsIpRanges.Url, invalidCount);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in AWS IP ranges from {Url}", sources.AwsIpRanges.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch AWS IP ranges from {Url}", sources.AwsIpRanges.Url);
            }

        // Fetch GCP IP ranges
        if (sources.GcpIpRanges.Enabled && !string.IsNullOrEmpty(sources.GcpIpRanges.Url))
            try
            {
                var gcpJson = await client.GetStringAsync(sources.GcpIpRanges.Url, cancellationToken);
                var gcpData = JsonSerializer.Deserialize<GcpIpRangesResponse>(gcpJson, JsonOptions);
                if (gcpData?.Prefixes != null)
                {
                    var validCount = 0;
                    var invalidCount = 0;
                    foreach (var p in gcpData.Prefixes)
                    {
                        if (!string.IsNullOrEmpty(p.Ipv4Prefix))
                        {
                            if (IsValidCidr(p.Ipv4Prefix))
                            {
                                ranges.Add(p.Ipv4Prefix);
                                validCount++;
                            }
                            else
                            {
                                invalidCount++;
                            }
                        }

                        if (!string.IsNullOrEmpty(p.Ipv6Prefix))
                        {
                            if (IsValidCidr(p.Ipv6Prefix))
                            {
                                ranges.Add(p.Ipv6Prefix);
                                validCount++;
                            }
                            else
                            {
                                invalidCount++;
                            }
                        }
                    }

                    _logger.LogInformation(
                        "Fetched {ValidCount} valid GCP IP ranges from {Url} ({InvalidCount} rejected)",
                        validCount, sources.GcpIpRanges.Url, invalidCount);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in GCP IP ranges from {Url}", sources.GcpIpRanges.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch GCP IP ranges from {Url}", sources.GcpIpRanges.Url);
            }

        // Fetch Azure IP ranges (if configured - URL changes weekly)
        if (sources.AzureIpRanges.Enabled && !string.IsNullOrEmpty(sources.AzureIpRanges.Url))
            try
            {
                var azureJson = await client.GetStringAsync(sources.AzureIpRanges.Url, cancellationToken);
                var azureData = JsonSerializer.Deserialize<AzureIpRangesResponse>(azureJson, JsonOptions);
                if (azureData?.Values != null)
                {
                    var validCount = 0;
                    var invalidCount = 0;
                    foreach (var value in azureData.Values)
                        if (value.Properties?.AddressPrefixes != null)
                            foreach (var prefix in value.Properties.AddressPrefixes)
                                if (IsValidCidr(prefix))
                                {
                                    ranges.Add(prefix);
                                    validCount++;
                                }
                                else
                                {
                                    invalidCount++;
                                }

                    _logger.LogInformation(
                        "Fetched {ValidCount} valid Azure IP ranges from {Url} ({InvalidCount} rejected)",
                        validCount, sources.AzureIpRanges.Url, invalidCount);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in Azure IP ranges from {Url}", sources.AzureIpRanges.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Azure IP ranges from {Url}", sources.AzureIpRanges.Url);
            }

        // Fetch Cloudflare IPv4 ranges
        if (sources.CloudflareIpv4.Enabled && !string.IsNullOrEmpty(sources.CloudflareIpv4.Url))
            try
            {
                var cfIpv4 = await client.GetStringAsync(sources.CloudflareIpv4.Url, cancellationToken);
                var lines = cfIpv4.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var validCount = 0;
                var invalidCount = 0;
                foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    var trimmed = line.Trim();
                    if (IsValidCidr(trimmed))
                    {
                        ranges.Add(trimmed);
                        validCount++;
                    }
                    else
                    {
                        invalidCount++;
                    }
                }

                _logger.LogInformation(
                    "Fetched {ValidCount} valid Cloudflare IPv4 ranges from {Url} ({InvalidCount} rejected)",
                    validCount, sources.CloudflareIpv4.Url, invalidCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Cloudflare IPv4 ranges from {Url}", sources.CloudflareIpv4.Url);
            }

        // Fetch Cloudflare IPv6 ranges
        if (sources.CloudflareIpv6.Enabled && !string.IsNullOrEmpty(sources.CloudflareIpv6.Url))
            try
            {
                var cfIpv6 = await client.GetStringAsync(sources.CloudflareIpv6.Url, cancellationToken);
                var lines = cfIpv6.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var validCount = 0;
                var invalidCount = 0;
                foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
                {
                    var trimmed = line.Trim();
                    if (IsValidCidr(trimmed))
                    {
                        ranges.Add(trimmed);
                        validCount++;
                    }
                    else
                    {
                        invalidCount++;
                    }
                }

                _logger.LogInformation(
                    "Fetched {ValidCount} valid Cloudflare IPv6 ranges from {Url} ({InvalidCount} rejected)",
                    validCount, sources.CloudflareIpv6.Url, invalidCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Cloudflare IPv6 ranges from {Url}", sources.CloudflareIpv6.Url);
            }

        var result = ranges.ToList();

        if (result.Count == 0)
        {
            _logger.LogWarning("No IP ranges fetched from any source, using fallback ranges");
            result = GetFallbackDatacenterRanges();
        }
        else
        {
            _logger.LogInformation("Total unique datacenter IP ranges: {Count}", result.Count);
        }

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    /// <summary>
    ///     Fetches Matomo bot patterns with metadata (name, category, url).
    ///     Only fetches if Matomo source is enabled in configuration.
    /// </summary>
    public async Task<List<BotPattern>> GetMatomoBotPatternsAsync(CancellationToken cancellationToken = default)
    {
        const string cacheKey = "bot_list_matomo_patterns";

        if (_cache.TryGetValue<List<BotPattern>>(cacheKey, out var cached) && cached != null) return cached;

        var sources = _options.DataSources;

        if (!sources.Matomo.Enabled || string.IsNullOrEmpty(sources.Matomo.Url))
        {
            _logger.LogDebug("Matomo data source is disabled");
            return GetFallbackMatomoPatterns();
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(_options.ListDownloadTimeoutSeconds);

            var yaml = await client.GetStringAsync(sources.Matomo.Url, cancellationToken);
            var patterns = ParseMatomoYaml(yaml);

            _logger.LogInformation("Fetched {Count} Matomo bot patterns from {Url}", patterns.Count,
                sources.Matomo.Url);

            _cache.Set(cacheKey, patterns, CacheDuration);
            return patterns;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Matomo patterns from {Url}, using fallback", sources.Matomo.Url);
            return GetFallbackMatomoPatterns();
        }
    }

    // ==========================================
    // Security Tool Pattern Sources
    // ==========================================

    /// <summary>
    ///     Fetches security tool patterns from configured sources.
    ///     Sources: digininja/scanner_user_agents, OWASP CoreRuleSet.
    /// </summary>
    public async Task<List<SecurityToolPattern>> GetSecurityToolPatternsAsync(
        CancellationToken cancellationToken = default)
    {
        const string cacheKey = "bot_list_security_tools";

        if (_cache.TryGetValue<List<SecurityToolPattern>>(cacheKey, out var cached) && cached != null)
            return cached;

        var allPatterns = new Dictionary<string, SecurityToolPattern>(StringComparer.OrdinalIgnoreCase);
        var sources = _options.DataSources;
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(_options.ListDownloadTimeoutSeconds);

        // Fetch digininja/scanner_user_agents (JSON format with metadata)
        // Note: This source provides full UAs (often spoofed) and scanner names.
        // We extract scanner names as patterns since full UAs are often indistinguishable from browsers.
        if (sources.ScannerUserAgents.Enabled && !string.IsNullOrEmpty(sources.ScannerUserAgents.Url))
            try
            {
                var json = await client.GetStringAsync(sources.ScannerUserAgents.Url, cancellationToken);
                var entries = JsonSerializer.Deserialize<List<ScannerUserAgentEntry>>(json, JsonOptions);

                if (entries != null)
                {
                    var validCount = 0;
                    // Extract unique scanner names as patterns (case-insensitive)
                    var scannerNames = entries
                        .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                        .Select(e => e.Name!.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    foreach (var scannerName in scannerNames)
                    {
                        // Use scanner name as pattern (lowercase for consistency)
                        var pattern = scannerName.ToLowerInvariant();
                        if (!allPatterns.ContainsKey(pattern))
                        {
                            allPatterns[pattern] = new SecurityToolPattern
                            {
                                Pattern = pattern,
                                Name = scannerName,
                                Category = "SecurityTool",
                                IsRegex = false, // Simple substring match
                                Source = "digininja/scanner_user_agents"
                            };
                            validCount++;
                        }
                    }

                    _logger.LogInformation("Extracted {Count} unique scanner names from {Url}",
                        validCount, sources.ScannerUserAgents.Url);
                }
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException ||
                                                   cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Scanner user agents fetch timed out or was canceled - using fallback patterns");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Scanner user agents fetch failed (network error) - using fallback patterns");
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Invalid JSON in scanner_user_agents from {Url}", sources.ScannerUserAgents.Url);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch scanner_user_agents from {Url}", sources.ScannerUserAgents.Url);
            }

        // Fetch OWASP CoreRuleSet scanners (text format, one pattern per line)
        if (sources.CoreRuleSetScanners.Enabled && !string.IsNullOrEmpty(sources.CoreRuleSetScanners.Url))
            try
            {
                var text = await client.GetStringAsync(sources.CoreRuleSetScanners.Url, cancellationToken);
                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var validCount = 0;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    // Skip comments and empty lines
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                        continue;

                    if (!allPatterns.ContainsKey(trimmed))
                    {
                        allPatterns[trimmed] = new SecurityToolPattern
                        {
                            Pattern = trimmed,
                            Name = InferToolName(trimmed),
                            Category = "SecurityTool",
                            IsRegex = ContainsRegexChars(trimmed),
                            Source = "OWASP/CoreRuleSet"
                        };
                        validCount++;
                    }
                }

                _logger.LogInformation("Fetched {Count} security tool patterns from {Url}",
                    validCount, sources.CoreRuleSetScanners.Url);
            }
            catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException ||
                                                   cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("CoreRuleSet patterns fetch timed out or was canceled - using fallback patterns");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "CoreRuleSet patterns fetch failed (network error) - using fallback patterns");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch CoreRuleSet patterns from {Url}",
                    sources.CoreRuleSetScanners.Url);
            }

        // Always merge fallback patterns to ensure common tools are covered
        // (external sources may not include all well-known tools like Burp, Acunetix)
        foreach (var fallback in GetFallbackSecurityToolPatterns())
            if (!allPatterns.ContainsKey(fallback.Pattern))
                allPatterns[fallback.Pattern] = fallback;

        var result = allPatterns.Values.ToList();

        if (result.Count == 0)
            _logger.LogWarning("No security tool patterns available");
        else
            _logger.LogInformation("Total unique security tool patterns: {Count}", result.Count);

        _cache.Set(cacheKey, result, CacheDuration);
        return result;
    }

    private List<BotPattern> ParseMatomoYaml(string yaml)
    {
        // Simple YAML parser for the Matomo format
        // Format is: - regex: "pattern" / name: "BotName" / category: "Category"
        var patterns = new List<BotPattern>();
        var lines = yaml.Split('\n');

        BotPattern? currentPattern = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("- regex:"))
            {
                if (currentPattern != null) patterns.Add(currentPattern);

                currentPattern = new BotPattern
                {
                    Pattern = ExtractQuotedValue(trimmed, "regex:")
                };
            }
            else if (currentPattern != null)
            {
                if (trimmed.StartsWith("name:"))
                    currentPattern.Name = ExtractQuotedValue(trimmed, "name:");
                else if (trimmed.StartsWith("category:"))
                    currentPattern.Category = ExtractQuotedValue(trimmed, "category:");
                else if (trimmed.StartsWith("url:")) currentPattern.Url = ExtractQuotedValue(trimmed, "url:");
            }
        }

        if (currentPattern != null) patterns.Add(currentPattern);

        return patterns;
    }

    private string? ExtractQuotedValue(string line, string prefix)
    {
        var start = line.IndexOf(prefix) + prefix.Length;
        var text = line.Substring(start).Trim();

        // Remove quotes if present
        if (text.StartsWith('"') || text.StartsWith('\'')) text = text.Substring(1);
        if (text.EndsWith('"') || text.EndsWith('\'')) text = text.Substring(0, text.Length - 1);

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private List<string> GetFallbackCrawlerPatterns()
    {
        // Fallback to embedded list if download fails
        return new List<string>(BotSignatures.MaliciousBotPatterns
            .Concat(BotSignatures.AutomationFrameworks));
    }

    private List<string> GetFallbackDatacenterRanges()
    {
        // Basic fallback ranges
        return new List<string>
        {
            "3.0.0.0/8", "13.0.0.0/8", "18.0.0.0/8", "52.0.0.0/8", // AWS
            "20.0.0.0/8", "40.0.0.0/8", "104.0.0.0/8", // Azure
            "34.0.0.0/8", "35.0.0.0/8" // GCP
        };
    }

    private List<BotPattern> GetFallbackMatomoPatterns()
    {
        return BotSignatures.GoodBots.Select(kvp => new BotPattern
        {
            Name = kvp.Value,
            Pattern = kvp.Key,
            Category = "Search Engine"
        }).ToList();
    }

    /// <summary>
    ///     Validates a regex pattern for safety (prevents ReDoS attacks).
    ///     Rejects patterns that are too long, have excessive quantifiers, or fail to compile.
    /// </summary>
    private bool IsValidPattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        // Limit pattern length to prevent memory exhaustion
        if (pattern.Length > 500)
        {
            _logger.LogDebug("Rejected pattern exceeding max length: {Length} chars", pattern.Length);
            return false;
        }

        // Check for potentially dangerous patterns (excessive nested quantifiers)
        // These can cause catastrophic backtracking (ReDoS)
        if (DangerousQuantifierRegex().IsMatch(pattern))
        {
            _logger.LogDebug("Rejected pattern with nested possessive quantifiers: {Pattern}", pattern);
            return false;
        }

        try
        {
            // Try to compile the regex with a timeout to catch problematic patterns
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (RegexParseException ex)
        {
            _logger.LogDebug("Rejected invalid regex pattern: {Pattern} - {Error}", pattern, ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            _logger.LogDebug("Rejected invalid regex pattern: {Pattern} - {Error}", pattern, ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Validates a CIDR notation IP range for safety.
    ///     Ensures the format is valid IPv4 or IPv6 CIDR.
    /// </summary>
    private bool IsValidCidr(string cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return false;

        var trimmed = cidr.Trim();

        // Must contain exactly one /
        var parts = trimmed.Split('/');
        if (parts.Length != 2)
            return false;

        // Validate IP address part
        if (!IPAddress.TryParse(parts[0], out var ip))
            return false;

        // Validate prefix length
        if (!int.TryParse(parts[1], out var prefix))
            return false;

        // IPv4 prefix: 0-32, IPv6 prefix: 0-128
        var maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return prefix >= 0 && prefix <= maxPrefix;
    }

    /// <summary>
    ///     Infer tool name from pattern for display purposes.
    /// </summary>
    private static string InferToolName(string pattern)
    {
        // Known tool names to look for
        var knownTools = new[]
        {
            "sqlmap", "nikto", "nmap", "masscan", "gobuster", "feroxbuster", "ffuf", "wfuzz",
            "dirbuster", "dirb", "wpscan", "joomscan", "acunetix", "netsparker", "nessus",
            "openvas", "nuclei", "burp", "zap", "metasploit", "hydra", "medusa", "whatweb",
            "shodan", "censys", "arachni", "w3af", "qualys", "detectify"
        };

        var lowerPattern = pattern.ToLowerInvariant();
        foreach (var tool in knownTools)
            if (lowerPattern.Contains(tool))
                return char.ToUpperInvariant(tool[0]) + tool[1..];

        // Return first word or truncated pattern
        var firstWord = pattern.Split(' ', '/', '-', '_')[0];
        return firstWord.Length > 20 ? firstWord[..20] + "..." : firstWord;
    }

    /// <summary>
    ///     Check if pattern contains regex special characters.
    /// </summary>
    private static bool ContainsRegexChars(string pattern)
    {
        return pattern.Any(c =>
            c is '.' or '*' or '+' or '?' or '[' or ']' or '(' or ')' or '{' or '}' or '|' or '^' or '$' or '\\');
    }

    /// <summary>
    ///     Fallback security tool patterns if download fails.
    /// </summary>
    private static List<SecurityToolPattern> GetFallbackSecurityToolPatterns()
    {
        return new List<SecurityToolPattern>
        {
            new() { Pattern = "sqlmap", Name = "SQLMap", Category = "SqlInjection", Source = "fallback" },
            new() { Pattern = "nikto", Name = "Nikto", Category = "VulnerabilityScanner", Source = "fallback" },
            new() { Pattern = "nmap", Name = "Nmap", Category = "PortScanner", Source = "fallback" },
            new() { Pattern = "gobuster", Name = "Gobuster", Category = "DirectoryBruteForce", Source = "fallback" },
            new()
            {
                Pattern = "feroxbuster", Name = "FeroxBuster", Category = "DirectoryBruteForce", Source = "fallback"
            },
            new() { Pattern = "ffuf", Name = "FFUF", Category = "DirectoryBruteForce", Source = "fallback" },
            new() { Pattern = "wpscan", Name = "WPScan", Category = "CmsScanner", Source = "fallback" },
            new() { Pattern = "acunetix", Name = "Acunetix", Category = "VulnerabilityScanner", Source = "fallback" },
            new() { Pattern = "nessus", Name = "Nessus", Category = "VulnerabilityScanner", Source = "fallback" },
            new() { Pattern = "metasploit", Name = "Metasploit", Category = "ExploitFramework", Source = "fallback" },
            new() { Pattern = "burp", Name = "Burp Suite", Category = "WebProxy", Source = "fallback" },
            new() { Pattern = "owasp zap", Name = "OWASP ZAP", Category = "WebProxy", Source = "fallback" },
            new() { Pattern = "nuclei", Name = "Nuclei", Category = "VulnerabilityScanner", Source = "fallback" },
            new() { Pattern = "masscan", Name = "Masscan", Category = "PortScanner", Source = "fallback" },
            new() { Pattern = "hydra", Name = "Hydra", Category = "CredentialAttack", Source = "fallback" }
        };
    }

    [GeneratedRegex(@"\(\??\+\+|\*\+|\{\d+,\}\+")]
    private static partial Regex DangerousQuantifierRegex();

    // ==========================================
    // JSON models for parsing API responses
    // ==========================================

    // JSON models for parsing API responses (internal for source generator access)

    internal class CrawlerEntry
    {
        public string? Pattern { get; set; }
        public string? Url { get; set; }
        public string? Instances { get; set; }
    }

    // AWS IP ranges format: https://docs.aws.amazon.com/general/latest/gr/aws-ip-ranges.html
    internal class AwsIpRangesResponse
    {
        [JsonPropertyName("syncToken")] public string? SyncToken { get; set; }

        [JsonPropertyName("createDate")] public string? CreateDate { get; set; }

        [JsonPropertyName("prefixes")] public List<AwsPrefix>? Prefixes { get; set; }

        [JsonPropertyName("ipv6_prefixes")] public List<AwsIpv6Prefix>? Ipv6Prefixes { get; set; }
    }

    internal class AwsPrefix
    {
        [JsonPropertyName("ip_prefix")] public string? IpPrefix { get; set; }

        [JsonPropertyName("region")] public string? Region { get; set; }

        [JsonPropertyName("service")] public string? Service { get; set; }

        [JsonPropertyName("network_border_group")]
        public string? NetworkBorderGroup { get; set; }
    }

    internal class AwsIpv6Prefix
    {
        [JsonPropertyName("ipv6_prefix")] public string? Ipv6Prefix { get; set; }

        [JsonPropertyName("region")] public string? Region { get; set; }

        [JsonPropertyName("service")] public string? Service { get; set; }
    }

    // GCP IP ranges format: https://cloud.google.com/compute/docs/faq#find_ip_range
    internal class GcpIpRangesResponse
    {
        [JsonPropertyName("syncToken")] public string? SyncToken { get; set; }

        [JsonPropertyName("creationTime")] public string? CreationTime { get; set; }

        [JsonPropertyName("prefixes")] public List<GcpPrefix>? Prefixes { get; set; }
    }

    internal class GcpPrefix
    {
        [JsonPropertyName("ipv4Prefix")] public string? Ipv4Prefix { get; set; }

        [JsonPropertyName("ipv6Prefix")] public string? Ipv6Prefix { get; set; }

        [JsonPropertyName("service")] public string? Service { get; set; }

        [JsonPropertyName("scope")] public string? Scope { get; set; }
    }

    // Azure IP ranges format (simplified - actual format is more complex)
    internal class AzureIpRangesResponse
    {
        [JsonPropertyName("changeNumber")] public int? ChangeNumber { get; set; }

        [JsonPropertyName("cloud")] public string? Cloud { get; set; }

        [JsonPropertyName("values")] public List<AzureServiceTag>? Values { get; set; }
    }

    internal class AzureServiceTag
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("id")] public string? Id { get; set; }

        [JsonPropertyName("properties")] public AzureServiceTagProperties? Properties { get; set; }
    }

    internal class AzureServiceTagProperties
    {
        [JsonPropertyName("changeNumber")] public int? ChangeNumber { get; set; }

        [JsonPropertyName("region")] public string? Region { get; set; }

        [JsonPropertyName("platform")] public string? Platform { get; set; }

        [JsonPropertyName("systemService")] public string? SystemService { get; set; }

        [JsonPropertyName("addressPrefixes")] public List<string>? AddressPrefixes { get; set; }
    }

    // JSON model for digininja scanner_user_agents
    // Actual format: {"ua": "...", "scanner": "Nikto", "version": "...", "last seen": "..."}
    internal class ScannerUserAgentEntry
    {
        [JsonPropertyName("ua")] public string? Pattern { get; set; }

        [JsonPropertyName("scanner")] public string? Name { get; set; }

        [JsonPropertyName("category")] public string? Category { get; set; }

        [JsonPropertyName("version")] public string? Version { get; set; }

        [JsonPropertyName("last seen")] public string? LastSeen { get; set; }
    }
}