using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Result of verifying a bot's identity via IP ranges or FCrDNS.
/// </summary>
public sealed record VerifiedBotResult(
    string BotName,
    string VerificationMethod, // "ip_range", "fcrdns", "none"
    bool IsVerified);

/// <summary>
///     Configuration options for <see cref="VerifiedBotRegistry"/>.
///     Bound from appsettings.json: BotDetection:VerifiedBotRegistry
///     Defaults match the YAML manifest (verifiedbot.detector.yaml).
/// </summary>
public sealed class VerifiedBotRegistryOptions
{
    /// <summary>DNS cache TTL for verified results (hours). Bot DNS is stable for years; 24h is conservative.</summary>
    public double DnsVerifiedCacheTtlHours { get; set; } = 24;

    /// <summary>DNS cache TTL for failed results (hours). Short so transient failures recover.</summary>
    public double DnsFailedCacheTtlHours { get; set; } = 1;

    /// <summary>IP range list refresh interval (hours). Bot operators rarely update published ranges.</summary>
    public double IpRangeRefreshHours { get; set; } = 24;

    /// <summary>DNS lookup timeout (ms). Prevents slow DNS servers from blocking requests.</summary>
    public int DnsTimeoutMs { get; set; } = 5000;
}

/// <summary>
///     Singleton service that verifies bot identity using published IP ranges and FCrDNS.
///     Bots claim identity via User-Agent, but UA is trivially spoofable.
///     This service verifies claims by checking:
///     1. Published CIDR ranges (Google, Bing, OpenAI) — instant O(n) lookup
///     2. Forward-Confirmed reverse DNS (FCrDNS) for bots without published ranges
///     IP ranges are refreshed periodically via a background timer.
///     DNS verified results cached (configurable), failed results cached shorter.
///     All timing values configurable via appsettings.json: BotDetection:VerifiedBotRegistry
/// </summary>
public sealed class VerifiedBotRegistry : IHostedService, IDisposable
{
    private readonly ILogger<VerifiedBotRegistry> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // DNS cache: key = "ip:suffixPattern" to prevent cross-bot cache pollution
    private readonly ConcurrentDictionary<string, (bool verified, string? hostname, DateTimeOffset expiry)> _dnsCache = new();
    private readonly ConcurrentDictionary<string, List<IPNetwork>> _ipRanges = new();

    private Timer? _refreshTimer;
    private int _refreshing; // Guard against overlapping refreshes

    // Configurable timing — bound from VerifiedBotRegistryOptions (appsettings.json)
    private readonly TimeSpan _dnsVerifiedCacheTtl;
    private readonly TimeSpan _dnsFailedCacheTtl;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _dnsTimeout;

    /// <summary>
    ///     Bot definitions with verification methods.
    ///     Order matters: first match wins, so more specific patterns should come first.
    /// </summary>
    private static readonly BotDefinition[] BotDefinitions =
    [
        // === Bots with BOTH published IP ranges AND FCrDNS fallback ===
        new("Googlebot", "Googlebot",
            "https://developers.google.com/static/search/apis/ipranges/googlebot.json",
            ["*.googlebot.com", "*.google.com"]),
        new("Bingbot", "bingbot",
            "https://www.bing.com/toolbox/bingbot.json",
            ["*.search.msn.com"]),

        // === Bots with published IP ranges only (no FCrDNS) ===
        new("GPTBot", "GPTBot",
            "https://openai.com/gptbot-ranges.json",
            null),
        new("ChatGPT-User", "ChatGPT-User",
            "https://openai.com/gptbot-ranges.json", // Same IP ranges as GPTBot
            null),

        // === Bots with FCrDNS verification only ===
        new("DuckDuckBot", "DuckDuckBot",
            null,
            ["*.duckduckgo.com"]),
        new("Applebot", "Applebot",
            null,
            ["*.applebot.apple.com"]),
        new("YandexBot", "YandexBot",
            null,
            ["*.yandex.ru", "*.yandex.net", "*.yandex.com"]),
        new("Baiduspider", "Baiduspider",
            null,
            ["*.baidu.com", "*.baidu.jp"]),
        new("LinkedInBot", "LinkedInBot",
            null,
            ["*.linkedin.com"]),
    ];

    public VerifiedBotRegistry(
        ILogger<VerifiedBotRegistry> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<VerifiedBotRegistryOptions> options)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var opts = options.Value;
        _dnsVerifiedCacheTtl = TimeSpan.FromHours(opts.DnsVerifiedCacheTtlHours);
        _dnsFailedCacheTtl = TimeSpan.FromHours(opts.DnsFailedCacheTtlHours);
        _refreshInterval = TimeSpan.FromHours(opts.IpRangeRefreshHours);
        _dnsTimeout = TimeSpan.FromMilliseconds(opts.DnsTimeoutMs);
    }

    /// <summary>
    ///     Verify whether the given request is from a known bot based on UA pattern + IP verification.
    ///     Returns null if the UA doesn't match any known bot pattern.
    /// </summary>
    public async Task<VerifiedBotResult?> VerifyBotAsync(string? userAgent, string? clientIp)
    {
        if (string.IsNullOrWhiteSpace(userAgent) || string.IsNullOrWhiteSpace(clientIp))
            return null;

        var matchedBot = FindBotByUserAgent(userAgent);
        if (matchedBot == null)
            return null;

        // Try IP range verification first (instant, O(n) CIDR checks, no I/O)
        if (_ipRanges.TryGetValue(matchedBot.Name, out var ranges) && ranges.Count > 0)
        {
            if (IPAddress.TryParse(clientIp, out var ip))
            {
                foreach (var network in ranges)
                {
                    if (network.Contains(ip))
                        return new VerifiedBotResult(matchedBot.Name, "ip_range", true);
                }
            }

            // Has IP ranges loaded but IP didn't match any.
            // If this bot ALSO has FCrDNS domains, try that as fallback.
            // Otherwise, the IP is definitely not from this bot — spoofed.
            if (matchedBot.FcrDnsDomains is not { Length: > 0 })
                return new VerifiedBotResult(matchedBot.Name, "ip_range", false);
        }

        // Try FCrDNS verification (cached DNS lookups, ~50-100ms uncached)
        if (matchedBot.FcrDnsDomains is { Length: > 0 })
        {
            var verified = await VerifyFcrDnsAsync(clientIp, matchedBot.FcrDnsDomains);
            return new VerifiedBotResult(matchedBot.Name, "fcrdns", verified);
        }

        // No verification method available (shouldn't happen with current definitions)
        return new VerifiedBotResult(matchedBot.Name, "none", false);
    }

    /// <summary>
    ///     Synchronous quick check — returns the bot name if UA matches, null otherwise.
    ///     Does NOT verify IP. Use <see cref="VerifyBotAsync"/> for full verification.
    /// </summary>
    public string? MatchBotUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;
        return FindBotByUserAgent(userAgent)?.Name;
    }

    private static BotDefinition? FindBotByUserAgent(string userAgent)
    {
        foreach (var bot in BotDefinitions)
        {
            if (userAgent.Contains(bot.UaPattern, StringComparison.OrdinalIgnoreCase))
                return bot;
        }
        return null;
    }

    /// <summary>
    ///     Forward-Confirmed reverse DNS verification (FCrDNS).
    ///     1. Reverse DNS: IP → hostname (PTR record)
    ///     2. Check hostname suffix matches expected domains
    ///     3. Forward DNS: hostname → IP (A/AAAA record)
    ///     4. Verify the original IP appears in the forward result
    ///     This prevents DNS spoofing — an attacker can set a PTR record for their IP
    ///     to claim "googlebot.com", but the forward lookup of that hostname won't resolve
    ///     back to the attacker's IP.
    /// </summary>
    private async Task<bool> VerifyFcrDnsAsync(string clientIp, string[] allowedDomainPatterns)
    {
        // Cache key includes the first domain pattern to prevent cross-bot cache pollution.
        // Without this, a Googlebot IP verified against *.googlebot.com could incorrectly
        // return true for a YandexBot check against *.yandex.ru from the same IP.
        var cacheKey = $"{clientIp}:{allowedDomainPatterns[0]}";

        if (_dnsCache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTimeOffset.UtcNow)
            return cached.verified;

        try
        {
            if (!IPAddress.TryParse(clientIp, out var ip))
            {
                CacheDnsResult(cacheKey, false, null);
                return false;
            }

            // Step 1: Reverse DNS (PTR lookup) with timeout
            using var cts = new CancellationTokenSource(_dnsTimeout);
            IPHostEntry hostEntry;
            try
            {
                hostEntry = await Dns.GetHostEntryAsync(clientIp, cts.Token);
            }
            catch (SocketException)
            {
                CacheDnsResult(cacheKey, false, null);
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("FCrDNS reverse lookup timed out for {MaskedIP}", MaskIp(clientIp));
                CacheDnsResult(cacheKey, false, null);
                return false;
            }

            var hostname = hostEntry.HostName;

            // Some platforms return the IP string itself when no PTR record exists
            if (string.IsNullOrEmpty(hostname) ||
                hostname.Equals(clientIp, StringComparison.Ordinal))
            {
                CacheDnsResult(cacheKey, false, null);
                return false;
            }

            // Step 2: Check hostname suffix against allowed domains
            var hostnameLower = hostname.ToLowerInvariant();
            var matchesDomain = false;
            foreach (var pattern in allowedDomainPatterns)
            {
                // Pattern format: "*.googlebot.com" → suffix ".googlebot.com"
                var suffix = pattern.StartsWith("*.")
                    ? pattern[1..].ToLowerInvariant()  // ".googlebot.com"
                    : $".{pattern.ToLowerInvariant()}"; // "googlebot.com" → ".googlebot.com"

                if (hostnameLower.EndsWith(suffix, StringComparison.Ordinal) ||
                    hostnameLower.Equals(suffix.TrimStart('.'), StringComparison.Ordinal))
                {
                    matchesDomain = true;
                    break;
                }
            }

            if (!matchesDomain)
            {
                _logger.LogDebug(
                    "FCrDNS hostname {Hostname} does not match any allowed domain pattern",
                    hostname);
                CacheDnsResult(cacheKey, false, hostname);
                return false;
            }

            // Step 3: Forward DNS — confirm hostname resolves back to the original IP
            using var fwdCts = new CancellationTokenSource(_dnsTimeout);
            IPAddress[] forwardAddresses;
            try
            {
                forwardAddresses = await Dns.GetHostAddressesAsync(hostname, fwdCts.Token);
            }
            catch (SocketException)
            {
                CacheDnsResult(cacheKey, false, hostname);
                return false;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("FCrDNS forward lookup timed out for hostname {Hostname}", hostname);
                CacheDnsResult(cacheKey, false, hostname);
                return false;
            }

            // Step 4: Verify the original IP is in the forward result
            // Handle IPv4-mapped IPv6 addresses (e.g., ::ffff:66.249.66.1 vs 66.249.66.1)
            var normalizedIp = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            var verified = forwardAddresses.Any(a =>
            {
                var normalizedFwd = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
                return normalizedFwd.Equals(normalizedIp);
            });

            if (!verified)
            {
                _logger.LogDebug(
                    "FCrDNS forward lookup for {Hostname} did not return expected IP — possible PTR spoof",
                    hostname);
            }

            CacheDnsResult(cacheKey, verified, hostname);
            return verified;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FCrDNS verification failed for {MaskedIP}", MaskIp(clientIp));
            CacheDnsResult(cacheKey, false, null);
            return false;
        }
    }

    private void CacheDnsResult(string cacheKey, bool verified, string? hostname)
    {
        var ttl = verified ? _dnsVerifiedCacheTtl : _dnsFailedCacheTtl;
        _dnsCache[cacheKey] = (verified, hostname, DateTimeOffset.UtcNow + ttl);
    }

    /// <summary>
    ///     Fetch and parse IP ranges from a published JSON endpoint, assigning the result
    ///     to all bots that share the same URL (e.g. GPTBot + ChatGPT-User both use OpenAI's list).
    ///     Handles the common JSON format: { "prefixes": [{ "ipv4Prefix": "...", "ipv6Prefix": "..." }] }
    ///     Also handles Bing format with "ipPrefix" key.
    /// </summary>
    private async Task FetchIpRangesForUrlAsync(string url, string[] botNames)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("VerifiedBot");
            using var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to fetch IP ranges from {Url}: HTTP {Status} (bots: {Bots})",
                    url, response.StatusCode, string.Join(", ", botNames));
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var networks = new List<IPNetwork>();

            if (doc.RootElement.TryGetProperty("prefixes", out var prefixes))
            {
                foreach (var prefix in prefixes.EnumerateArray())
                {
                    string? cidr = null;

                    // Google format: { "ipv4Prefix": "..." } / { "ipv6Prefix": "..." }
                    if (prefix.TryGetProperty("ipv4Prefix", out var v4))
                        cidr = v4.GetString();
                    else if (prefix.TryGetProperty("ipv6Prefix", out var v6))
                        cidr = v6.GetString();
                    // Bing format: { "ipPrefix": "..." }
                    else if (prefix.TryGetProperty("ipPrefix", out var ipPrefix))
                        cidr = ipPrefix.GetString();

                    if (!string.IsNullOrEmpty(cidr) && IPNetwork.TryParse(cidr, out var network))
                        networks.Add(network);
                }
            }

            if (networks.Count > 0)
            {
                // Assign the same parsed ranges to all bots sharing this URL
                foreach (var name in botNames)
                    _ipRanges[name] = networks;

                _logger.LogInformation("Loaded {Count} IP ranges from {Url} for {Bots}",
                    networks.Count, url, string.Join(", ", botNames));
            }
            else
            {
                _logger.LogWarning("No valid IP ranges found in response from {Url}", url);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch IP ranges from {Url}", url);
        }
    }

    private async Task RefreshAllRangesAsync()
    {
        // Guard against overlapping refreshes from timer + manual calls
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;

        try
        {
            _logger.LogInformation("Refreshing verified bot IP ranges...");

            // Group by URL to avoid fetching the same endpoint multiple times
            // (e.g. GPTBot and ChatGPT-User share the same OpenAI ranges URL)
            var urlGroups = BotDefinitions
                .Where(b => !string.IsNullOrEmpty(b.IpRangeUrl))
                .GroupBy(b => b.IpRangeUrl!)
                .Select(g => FetchIpRangesForUrlAsync(g.Key, g.Select(b => b.Name).ToArray()));
            await Task.WhenAll(urlGroups);

            // Prune expired DNS cache entries
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _dnsCache.Keys)
            {
                if (_dnsCache.TryGetValue(key, out var entry) && entry.expiry < now)
                    _dnsCache.TryRemove(key, out _);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Best-effort initial load — don't block app startup if IP range endpoints are down
        try
        {
            await RefreshAllRangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load IP ranges on startup — will retry in {Hours}h",
                _refreshInterval.TotalHours);
        }

        // Schedule periodic refresh
        _refreshTimer = new Timer(
            static state => _ = ((VerifiedBotRegistry)state!).SafeRefreshAsync(),
            this,
            _refreshInterval,
            _refreshInterval);
    }

    private async Task SafeRefreshAsync()
    {
        try
        {
            await RefreshAllRangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic IP range refresh failed — will retry in {Hours}h",
                _refreshInterval.TotalHours);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _refreshTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }

    /// <summary>
    ///     Mask an IP for logging (zero-PII). Shows first octet only for IPv4.
    /// </summary>
    private static string MaskIp(string ip)
    {
        var dotIndex = ip.IndexOf('.');
        return dotIndex > 0 ? $"{ip[..dotIndex]}.*.*.*" : "***";
    }

    private sealed record BotDefinition(
        string Name,
        string UaPattern,
        string? IpRangeUrl,
        string[]? FcrDnsDomains);
}
