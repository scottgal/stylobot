using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Definitions.BotPatterns;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Result of verifying a bot's identity via IP ranges or FCrDNS.
/// </summary>
public sealed record VerifiedBotResult(
    string BotName,
    string VerificationMethod, // "ip_range", "fcrdns", "none"
    bool IsVerified);

/// <summary>
///     Result of the honest-bot rDNS-suffix check used by
///     <see cref="VerifiedBotRegistry.VerifyHonestBotAsync"/>.
///     An "honest bot" is a UA carrying a +URL claim (Mastodon, Pleroma,
///     Akkoma, MostlylucidBot, etc.) whose client-IP rDNS resolves to a host
///     on the claimed domain. We never IP-range-verify these -- they run on
///     arbitrary cloud IPs -- so rDNS suffix-match is the only available
///     verification channel.
/// </summary>
/// <param name="ClaimedDomain">The lowercase domain extracted from the +URL
/// fragment of the UA (e.g. <c>mastodon.example.org</c>).</param>
/// <param name="ResolvedHostname">The rDNS PTR result for the client IP. Null
/// when no PTR record exists (in which case the caller should not synthesise
/// a verdict at all -- absence of rDNS is ambiguous, not spoofed).</param>
/// <param name="SuffixMatched"><c>true</c> when the resolved hostname is equal
/// to, or a sub-domain of, the claimed domain.</param>
/// <param name="VerificationMethod">Either <c>"fcrdns"</c> on a suffix match
/// or <c>"fcrdns_mismatch"</c> when rDNS resolved to a different domain.</param>
public sealed record HonestBotResult(
    string ClaimedDomain,
    string ResolvedHostname,
    bool SuffixMatched,
    string VerificationMethod);

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
///     1. Published CIDR ranges (Google, Bing, OpenAI) - instant O(n) lookup
///     2. Forward-Confirmed reverse DNS (FCrDNS) for bots without published ranges
///     IP ranges are refreshed periodically via the project-wide
///     <see cref="IScheduleCoordinator"/> (Tick1h cadence, gated on
///     "last-success older than configured interval"). The in-memory
///     <c>_ipRanges</c> dictionary is rebuilt from the published JSON
///     endpoints on each successful refresh; loss-on-restart is
///     covered because detection works fine while the dictionary is
///     populating (matching the pre-Wave-2 "fire-and-forget initial
///     load" semantics).
///     DNS verified results cached (configurable), failed results cached shorter.
///     All timing values configurable via appsettings.json: BotDetection:VerifiedBotRegistry
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was an
///         <see cref="Microsoft.Extensions.Hosting.IHostedService"/> using a
///         private <see cref="Timer"/> for the refresh cadence; now subscribes
///         to <see cref="TickCadence.Tick1h"/> and gates the fetch on
///         "last-success older than <see cref="VerifiedBotRegistryOptions.IpRangeRefreshHours"/>".
///         The very first eligible tick after boot still primes the
///         dictionary (<see cref="_lastSuccessfulRefreshUtc"/> is null on cold
///         start, so the "not yet due" guard short-circuits to a refresh).
///         See <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class VerifiedBotRegistry : IDisposable
{
    private readonly ILogger<VerifiedBotRegistry> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // DNS cache: key = "ip:suffixPattern" (bounded, TTL from config, LRU eviction at 10K entries)
    private readonly BoundedCache<string, (bool verified, string? hostname)> _dnsCache = new(maxSize: 10_000, defaultTtl: TimeSpan.FromHours(1));
    private readonly ConcurrentDictionary<string, List<IPNetwork>> _ipRanges = new();

    private readonly IDisposable _subscription;
    private int _refreshing; // Guard against overlapping refreshes
    private DateTime? _lastSuccessfulRefreshUtc;
    private int _disposed;

    // Configurable timing - bound from VerifiedBotRegistryOptions (appsettings.json)
    private readonly TimeSpan _dnsVerifiedCacheTtl;
    private readonly TimeSpan _dnsFailedCacheTtl;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _dnsTimeout;

    /// <summary>
    ///     Test seam: overrides the reverse-DNS resolver used by
    ///     <see cref="VerifyHonestBotAsync"/>. Production code leaves this
    ///     null and we fall through to <see cref="Dns.GetHostEntryAsync(string, CancellationToken)"/>.
    ///     Internal so test assemblies (Mostlylucid.BotDetection.Test via
    ///     <c>InternalsVisibleTo</c>) can substitute a deterministic stub
    ///     without spinning up real DNS in CI.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>>? RdnsResolverOverride { get; set; }

    // Regex shared with VerifiedBotContributor.CheckHonestBot for extracting the
    // host portion of a "+https://example.org/path" UA fragment. Kept inline as a
    // compiled regex (not source-generated) because the registry is a singleton
    // and we want a single compiled pattern across the assembly.
    private static readonly Regex UaDomainRegex = new(
        @"https?://([a-zA-Z0-9][-a-zA-Z0-9]*(?:\.[a-zA-Z0-9][-a-zA-Z0-9]*)+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Bot definitions with verification methods.
    ///     Loaded from YAML bot-pattern files (entries with ip_ranges_url or verified_domains).
    ///     Order matters: first match wins, so more specific patterns should come first.
    ///     To add a verifiable bot, add it to the appropriate YAML file in Definitions/BotPatterns/.
    /// </summary>
    private readonly BotDefinition[] _botDefinitions;

    public VerifiedBotRegistry(
        ILogger<VerifiedBotRegistry> logger,
        IHttpClientFactory httpClientFactory,
        IOptions<VerifiedBotRegistryOptions> options,
        IScheduleCoordinator coordinator,
        BotPatternLoader? patternLoader = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var opts = options.Value;
        _dnsVerifiedCacheTtl = TimeSpan.FromHours(opts.DnsVerifiedCacheTtlHours);
        _dnsFailedCacheTtl = TimeSpan.FromHours(opts.DnsFailedCacheTtlHours);
        _refreshInterval = TimeSpan.FromHours(opts.IpRangeRefreshHours);
        _dnsTimeout = TimeSpan.FromMilliseconds(opts.DnsTimeoutMs);

        var loader = patternLoader ?? BotPatternLoader.Default;
        _botDefinitions = loader.VerifiablePatterns
            .Select(p => new BotDefinition(
                p.BotName,
                p.Pattern,
                p.IpRangesUrl,
                p.VerifiedDomains))
            .ToArray();

        // Diagnostic: log the pattern count so we can confirm at startup whether
        // BotPatternLoader is actually reading the embedded YAML files. The
        // VerifiedBotContributor relies on this list -- if it is empty, every
        // verifiedbot.* signal goes unwritten and the verification badge can
        // never graduate to the green tick. Logging at INFO so it shows up
        // without enabling debug.
        _logger.LogInformation(
            "VerifiedBotRegistry initialised with {Count} bot definitions (sample: {Sample})",
            _botDefinitions.Length,
            string.Join(",", _botDefinitions.Take(5).Select(b => b.Name)));

        _subscription = coordinator.Subscribe(
            TickCadence.Tick1h,
            "VerifiedBotRegistry",
            CostHint.Medium,
            OnTickAsync);
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
            // Otherwise, the IP is definitely not from this bot - spoofed.
            if (matchedBot.FcrDnsDomains is not { Length: > 0 })
                return new VerifiedBotResult(matchedBot.Name, "ip_range", false);
        }

        // Try FCrDNS verification (cached DNS lookups, ~50-100ms uncached)
        if (matchedBot.FcrDnsDomains is { Length: > 0 })
        {
            var verified = await VerifyFcrDnsAsync(clientIp, matchedBot.FcrDnsDomains);
            // null = the DNS check couldn't run (transient failure/timeout). That is a
            // MISSING signal, not a refutation -- report it as "none" (unverifiable) so
            // the consumer does NOT brand it spoofed. Only a deterministic true/false
            // is a real FCrDNS verdict.
            return verified is null
                ? new VerifiedBotResult(matchedBot.Name, "none", false)
                : new VerifiedBotResult(matchedBot.Name, "fcrdns", verified.Value);
        }

        // No verification method available (shouldn't happen with current definitions)
        return new VerifiedBotResult(matchedBot.Name, "none", false);
    }

    /// <summary>
    ///     Synchronous IP-range-only verification. Skips the FCrDNS fallback so the
    ///     check is safe to call from the inline detector path (the default
    ///     policy excludes the full <see cref="VerifyBotAsync"/> because rDNS is
    ///     too slow inline).
    /// </summary>
    /// <returns>
    ///     <list type="bullet">
    ///         <item><c>null</c> -- UA doesn't match any known bot pattern, OR the
    ///               matched bot has only FCrDNS domains and no published IP ranges
    ///               (deferred to the async path).</item>
    ///         <item><c>VerifiedBotResult{ IsVerified = true, VerificationMethod = "ip_range" }</c>
    ///               -- UA matches a known bot AND the client IP is in the bot's
    ///               published range. Real Bingbot from Microsoft Azure lands here.</item>
    ///         <item><c>VerifiedBotResult{ IsVerified = false, VerificationMethod = "ip_range" }</c>
    ///               -- UA claims a known bot whose ranges loaded, but the IP isn't
    ///               in any of them. Live Amazonbot-impersonator from HK lands here.</item>
    ///     </list>
    /// </returns>
    public VerifiedBotResult? VerifyBotInline(string? userAgent, string? clientIp)
    {
        if (string.IsNullOrWhiteSpace(userAgent) || string.IsNullOrWhiteSpace(clientIp))
            return null;

        var matchedBot = FindBotByUserAgent(userAgent);
        if (matchedBot == null)
            return null;

        if (!_ipRanges.TryGetValue(matchedBot.Name, out var ranges) || ranges.Count == 0)
            return null; // IP ranges not yet loaded or bot only verifiable via FCrDNS.

        if (!IPAddress.TryParse(clientIp, out var ip))
            return null;

        foreach (var network in ranges)
        {
            if (network.Contains(ip))
                return new VerifiedBotResult(matchedBot.Name, "ip_range", true);
        }

        // Has IP ranges loaded but IP didn't match any. If FCrDNS is a fallback,
        // defer to the async path -- don't emit a "spoofed" verdict that the async
        // check could overturn. If the bot has NO FCrDNS fallback, the IP miss is
        // authoritative -- it's spoofed.
        if (matchedBot.FcrDnsDomains is { Length: > 0 })
            return null;

        return new VerifiedBotResult(matchedBot.Name, "ip_range", false);
    }

    /// <summary>
    ///     Honest-bot rDNS-after-the-fact verification for UAs that carry a
    ///     <c>+URL</c> claim (Mastodon / Pleroma / Akkoma instances, the
    ///     <c>MostlylucidBot</c> family, and anything that follows the
    ///     fediverse / "transparent operator" convention).
    ///     <para>
    ///     This is the bot-side analogue of <see cref="VerifyFcrDnsAsync"/>
    ///     for bots that don't publish CIDR ranges and aren't in the
    ///     <c>_botDefinitions</c> list (Mastodon runs on arbitrary cloud IPs,
    ///     so it deliberately has no <c>ip_ranges_url</c> / <c>verified_domains</c>).
    ///     Instead of comparing the rDNS hostname against a vendor allowlist,
    ///     we compare it against the domain the UA itself claimed -- if both
    ///     agree the operator was honest about identity.
    ///     </para>
    ///     <para>
    ///     <b>Lives in the registry, runs off the request hot path.</b>
    ///     Previously this logic was inline inside
    ///     <see cref="Orchestration.ContributingDetectors.VerifiedBotContributor.CheckHonestBot"/>,
    ///     gated by <c>skip_when: detection.early_exit</c> in the manifest
    ///     AND excluded from every <see cref="Policies.DetectionPolicy"/> because
    ///     rDNS is too slow inline. Net result: rDNS-after-the-fact for
    ///     fediverse-shaped traffic NEVER fired. Gap #1 in the claim-verify-trust
    ///     analysis (2026-06-15) -- fix per Option B (move rDNS off the
    ///     request path entirely; call from <see cref="BackgroundEnrichmentService"/>).
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <list type="bullet">
    ///         <item><c>null</c> when the UA carries no extractable +URL,
    ///               inputs are empty, or rDNS yielded nothing (no PTR record).
    ///               Absence of rDNS is ambiguous, not spoofed -- never
    ///               synthesise a verdict from missing data.</item>
    ///         <item><see cref="HonestBotResult"/> with
    ///               <c>VerificationMethod = "fcrdns"</c> and
    ///               <c>SuffixMatched = true</c> when the rDNS host is the
    ///               claimed domain or a sub-domain of it -- the operator
    ///               was honest about who they are.</item>
    ///         <item><see cref="HonestBotResult"/> with
    ///               <c>VerificationMethod = "fcrdns_mismatch"</c> when the
    ///               rDNS host is on a different domain than the UA claimed
    ///               (CDNs, shared hosting, EC2-style hostnames). Weaker
    ///               signal than a clean spoof -- callers should not block
    ///               on this alone.</item>
    ///     </list>
    /// </returns>
    public async Task<HonestBotResult?> VerifyHonestBotAsync(
        string? userAgent,
        string? clientIp,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userAgent) || string.IsNullOrWhiteSpace(clientIp))
            return null;

        var domainMatch = UaDomainRegex.Match(userAgent);
        if (!domainMatch.Success)
            return null;

        var claimedDomain = domainMatch.Groups[1].Value.ToLowerInvariant();

        // Reuse the existing FCrDNS cache under a distinct key prefix so we
        // don't collide with the verified-bot suffix entries.
        var cacheKey = $"honest:{clientIp}";
        string? hostname;
        if (_dnsCache.TryGet(cacheKey, out var cached))
        {
            hostname = cached.hostname;
        }
        else
        {
            hostname = await ResolveReverseDnsAsync(clientIp, ct);
            // Cache the resolved hostname (success or absence) so the next
            // enrichment pass for the same IP is free. Pair the success flag
            // with the hostname so we use the verified TTL when we got a
            // useful PTR back, the failed TTL when nothing came in.
            CacheDnsResult(cacheKey, verified: !string.IsNullOrEmpty(hostname), hostname);
        }

        if (string.IsNullOrEmpty(hostname))
            return null;

        var hostnameLower = hostname.TrimEnd('.').ToLowerInvariant();
        var matched = hostnameLower == claimedDomain ||
                      hostnameLower.EndsWith("." + claimedDomain, StringComparison.Ordinal);

        return new HonestBotResult(
            ClaimedDomain: claimedDomain,
            ResolvedHostname: hostnameLower,
            SuffixMatched: matched,
            VerificationMethod: matched ? "fcrdns" : "fcrdns_mismatch");
    }

    /// <summary>
    ///     Reverse-DNS the client IP. Honours <see cref="RdnsResolverOverride"/>
    ///     so tests can short-circuit the network call. Treats the platform
    ///     "no PTR" sentinel (hostname == ip-literal) as an empty result.
    /// </summary>
    private async Task<string?> ResolveReverseDnsAsync(string clientIp, CancellationToken ct)
    {
        if (RdnsResolverOverride is { } stub)
        {
            try
            {
                return await stub(clientIp, ct);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_dnsTimeout);
            var entry = await Dns.GetHostEntryAsync(clientIp, cts.Token);
            // Platforms return the IP literal itself when no PTR exists.
            if (string.IsNullOrEmpty(entry.HostName) ||
                entry.HostName.Equals(clientIp, StringComparison.Ordinal) ||
                IPAddress.TryParse(entry.HostName, out _))
                return null;
            return entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Honest-bot rDNS failed for {MaskedIP}", MaskIp(clientIp));
            return null;
        }
    }

    /// <summary>
    ///     Synchronous quick check - returns the bot name if UA matches, null otherwise.
    ///     Does NOT verify IP. Use <see cref="VerifyBotAsync"/> for full verification.
    /// </summary>
    public string? MatchBotUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return null;
        return FindBotByUserAgent(userAgent)?.Name;
    }

    private BotDefinition? FindBotByUserAgent(string userAgent)
    {
        foreach (var bot in _botDefinitions)
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
    ///     This prevents DNS spoofing - an attacker can set a PTR record for their IP
    ///     to claim "googlebot.com", but the forward lookup of that hostname won't resolve
    ///     back to the attacker's IP.
    /// </summary>
    // Returns true = FCrDNS verified, false = a DETERMINISTIC refutation (lookup ran,
    // no PTR / hostname mismatch / forward mismatch), null = the check could NOT run
    // (transient DNS failure or timeout). Null must NOT be read as spoofed -- "fail
    // trips bot" is the bug we guard against; a failed check is a missing signal.
    private async Task<bool?> VerifyFcrDnsAsync(string clientIp, string[] allowedDomainPatterns)
    {
        // Cache key includes the first domain pattern to prevent cross-bot cache pollution.
        // Without this, a Googlebot IP verified against *.googlebot.com could incorrectly
        // return true for a YandexBot check against *.yandex.ru from the same IP.
        var cacheKey = $"{clientIp}:{allowedDomainPatterns[0]}";

        if (_dnsCache.TryGet(cacheKey, out var cached))
            return cached.verified;

        try
        {
            if (!IPAddress.TryParse(clientIp, out var ip))
                // Unusable input, not a refutation -- couldn't verify. Don't poison the cache.
                return null;

            // Step 1: Reverse DNS (PTR lookup) with timeout
            using var cts = new CancellationTokenSource(_dnsTimeout);
            IPHostEntry hostEntry;
            try
            {
                hostEntry = await Dns.GetHostEntryAsync(clientIp, cts.Token);
            }
            catch (SocketException)
            {
                // DNS infrastructure failure -- our check couldn't run. Not a spoof; retry next time.
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("FCrDNS reverse lookup timed out for {MaskedIP}", MaskIp(clientIp));
                return null;
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

            // Step 3: Forward DNS - confirm hostname resolves back to the original IP
            using var fwdCts = new CancellationTokenSource(_dnsTimeout);
            IPAddress[] forwardAddresses;
            try
            {
                forwardAddresses = await Dns.GetHostAddressesAsync(hostname, fwdCts.Token);
            }
            catch (SocketException)
            {
                // Forward-lookup infrastructure failure -- couldn't run the confirm. Not a spoof.
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("FCrDNS forward lookup timed out for hostname {Hostname}", hostname);
                return null;
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
                    "FCrDNS forward lookup for {Hostname} did not return expected IP - possible PTR spoof",
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
        _dnsCache.Set(cacheKey, (verified, hostname), ttl);
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
            var urlGroups = _botDefinitions
                .Where(b => !string.IsNullOrEmpty(b.IpRangeUrl))
                .GroupBy(b => b.IpRangeUrl!)
                .Select(g => FetchIpRangesForUrlAsync(g.Key, g.Select(b => b.Name).ToArray()));
            await Task.WhenAll(urlGroups);

            // DNS cache is bounded with automatic LRU eviction via BoundedCache
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    /// <summary>
    ///     The coordinator's tick handler. Fires every Tick1h; gates the
    ///     IP-range fetch on "last-success older than configured
    ///     <see cref="VerifiedBotRegistryOptions.IpRangeRefreshHours"/>".
    ///     On boot, <see cref="_lastSuccessfulRefreshUtc"/> is null so the
    ///     first eligible tick refreshes immediately -- matching the
    ///     pre-Wave-2 fire-and-forget initial load. Public so tests can drive
    ///     a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        var lastSuccess = _lastSuccessfulRefreshUtc;
        if (lastSuccess != null && now.UtcDateTime - lastSuccess.Value < _refreshInterval)
            return; // Not yet due.

        try
        {
            await RefreshAllRangesAsync();
            _lastSuccessfulRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Periodic IP range refresh failed - will retry next tick");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    private static string MaskIp(string ip) => Helpers.PrivacyHelper.MaskIp(ip);

    private sealed record BotDefinition(
        string Name,
        string UaPattern,
        string? IpRangeUrl,
        string[]? FcrDnsDomains);
}