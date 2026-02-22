using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Dashboard;

/// <summary>
///     Multi-factor signature generation service for bot detection.
///     Generates MULTIPLE privacy-safe signatures for each request:
///     1. Primary Signature: HMAC(IP + UA) - main identity
///     2. IP Signature: HMAC(IP) - handles UA changes
///     3. UA Signature: HMAC(UA) - handles dynamic ISPs
///     4. Client-Side Signature: HMAC(ClientFingerprint) - browser identity
///     5. Plugin Signature: HMAC(Plugins) - stable across IP/UA changes
///     Pattern matching uses multi-factor correlation:
///     - Strict match: All signatures match → same client
///     - IP changed: UA + ClientSide + Plugins match → dynamic ISP
///     - UA changed: IP + ClientSide + Plugins match → browser update
///     - Avoid FP: Require 2+ factors to match
///     Protocol-aware: WebSocket upgrades and other reduced-header requests carry forward
///     known factors from the same PrimarySignature so factor count stays consistent.
/// </summary>
public sealed class MultiFactorSignatureService
{
    private readonly PiiHasher _hasher;
    private readonly ILogger<MultiFactorSignatureService> _logger;

    // Configuration
    private readonly int _minimumFactorsToMatch = 2; // Require at least 2 factors for pattern match

    // Carry-forward cache: when a request (e.g. WebSocket upgrade) produces fewer factors
    // than a previous request from the same client, inherit the richer factors.
    // Keyed by PrimarySignature → last known secondary factors.
    // Bounded to prevent unbounded growth; entries expire after 30 minutes.
    private readonly ConcurrentDictionary<string, CachedFactors> _factorCache = new();
    private const int MaxCacheEntries = 10_000;
    private int _evictionRunning;

    public MultiFactorSignatureService(
        PiiHasher hasher,
        ILogger<MultiFactorSignatureService> logger)
    {
        _hasher = hasher;
        _logger = logger;
    }

    /// <summary>
    ///     Generate multi-factor signatures from HTTP context.
    ///     Returns all possible signature factors for correlation.
    ///     Protocol-aware: when a request (WebSocket upgrade, SignalR negotiate) produces
    ///     fewer factors than a previous request from the same client, the richer factors
    ///     are carried forward. This ensures consistent signature identity across request
    ///     types without weakening detection — the PrimarySignature (IP+UA) anchors the
    ///     match, and secondary factors are only inherited, never fabricated.
    /// </summary>
    public MultiFactorSignatures GenerateSignatures(HttpContext httpContext)
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        // Extract client-side fingerprint from headers (if available)
        var clientFingerprint = ExtractClientFingerprint(httpContext);

        // Extract plugin signature from headers (if available)
        var pluginSignature = ExtractPluginSignature(httpContext);

        // Extract country code from GeoLocation (set by GeoRoutingMiddleware)
        var countryCode = ExtractCountryCode(httpContext);

        // Compute hashed factors
        var primarySig = _hasher.ComputeSignature(ip, userAgent);
        var clientSideSig = clientFingerprint != null ? _hasher.ComputeSignature(clientFingerprint) : null;
        var pluginSig = pluginSignature != null ? _hasher.ComputeSignature(pluginSignature) : null;
        var ipClientSig = clientFingerprint != null ? _hasher.ComputeSignature(ip, clientFingerprint) : null;
        var uaClientSig = clientFingerprint != null ? _hasher.ComputeSignature(userAgent, clientFingerprint) : null;

        // Carry-forward: non-document requests (WebSocket, fetch/XHR, SignalR negotiate)
        // produce different secondary factors (Accept-Encoding differs, Client Hints may be
        // absent). If we have a cached full-page-load set for this PrimarySignature, use those
        // factors instead — this keeps the signature identity stable across request types.
        //
        // Detect non-document requests: WebSocket upgrade, Sec-Fetch-Dest != document/iframe,
        // or missing any factors that the cache has.
        var isNonDocumentRequest = IsNonDocumentRequest(httpContext);
        var needsCarryForward = clientSideSig == null || pluginSig == null || isNonDocumentRequest;
        if (needsCarryForward)
        {
            if (_factorCache.TryGetValue(primarySig, out var cached) &&
                cached.Timestamp > DateTime.UtcNow.AddMinutes(-30))
            {
                if (isNonDocumentRequest)
                {
                    // For non-document requests, always prefer cached full-page factors
                    // because WebSocket/fetch send different Accept-Encoding etc.
                    clientSideSig = cached.ClientSideSignature ?? clientSideSig;
                    pluginSig = cached.PluginSignature ?? pluginSig;
                    ipClientSig = cached.IpClientSignature ?? ipClientSig;
                    uaClientSig = cached.UaClientSignature ?? uaClientSig;
                    countryCode = cached.GeoSignature ?? countryCode;
                }
                else
                {
                    // For document requests, only fill in missing factors
                    clientSideSig ??= cached.ClientSideSignature;
                    pluginSig ??= cached.PluginSignature;
                    ipClientSig ??= cached.IpClientSignature;
                    uaClientSig ??= cached.UaClientSignature;
                    countryCode ??= cached.GeoSignature;
                }

                _logger.LogDebug(
                    "Carried forward factors for {Primary} (protocol: {Protocol}, nonDocument: {NonDoc})",
                    primarySig[..Math.Min(8, primarySig.Length)],
                    httpContext.Request.Headers["Upgrade"].FirstOrDefault() ?? "HTTP",
                    isNonDocumentRequest);
            }
        }

        // Update cache: store the richest known factors for this PrimarySignature.
        // Non-document requests (WebSocket, XHR) should never overwrite cached document factors
        // because their headers (Accept-Encoding, etc.) differ from full page loads.
        if (!isNonDocumentRequest)
        {
            var currentFactorRichness = CountCachedRichness(clientSideSig, pluginSig, ipClientSig, uaClientSig, countryCode);
            _factorCache.AddOrUpdate(primarySig,
                _ => new CachedFactors(clientSideSig, pluginSig, ipClientSig, uaClientSig, countryCode, DateTime.UtcNow, IsFromDocumentRequest: true),
                (_, existing) =>
                {
                    // Always overwrite if existing was seeded by a non-document request
                    // (non-document headers differ from full page loads)
                    if (!existing.IsFromDocumentRequest)
                        return new CachedFactors(clientSideSig, pluginSig, ipClientSig, uaClientSig, countryCode, DateTime.UtcNow, IsFromDocumentRequest: true);

                    var existingRichness = CountCachedRichness(
                        existing.ClientSideSignature, existing.PluginSignature,
                        existing.IpClientSignature, existing.UaClientSignature, existing.GeoSignature);
                    // Only update if richer or same; always refresh timestamp
                    return currentFactorRichness >= existingRichness
                        ? new CachedFactors(clientSideSig, pluginSig, ipClientSig, uaClientSig, countryCode, DateTime.UtcNow, IsFromDocumentRequest: true)
                        : existing with { Timestamp = DateTime.UtcNow };
                });
        }
        else
        {
            // Non-document requests: only refresh timestamp, never overwrite factors.
            // If first request from this client, seed cache (marked as non-document so
            // the first real document request will overwrite).
            _factorCache.AddOrUpdate(primarySig,
                _ => new CachedFactors(clientSideSig, pluginSig, ipClientSig, uaClientSig, countryCode, DateTime.UtcNow, IsFromDocumentRequest: false),
                (_, existing) => existing with { Timestamp = DateTime.UtcNow });
        }

        // Evict stale entries periodically (single-thread guard to avoid contention)
        if (_factorCache.Count > MaxCacheEntries
            && Interlocked.CompareExchange(ref _evictionRunning, 1, 0) == 0)
        {
            try { EvictStaleEntries(); }
            finally { Interlocked.Exchange(ref _evictionRunning, 0); }
        }

        // Recompute factor count with carried-forward values
        var factorCount = 0;
        if (!string.IsNullOrEmpty(ip)) factorCount++;
        if (!string.IsNullOrEmpty(userAgent)) factorCount++;
        if (clientSideSig != null) factorCount++;
        if (pluginSig != null) factorCount++;
        if (!string.IsNullOrEmpty(countryCode)) factorCount++;

        var signatures = new MultiFactorSignatures
        {
            PrimarySignature = primarySig,
            IpSignature = !string.IsNullOrEmpty(ip) ? _hasher.HashIp(ip) : null,
            UaSignature = !string.IsNullOrEmpty(userAgent) ? _hasher.HashUserAgent(userAgent) : null,
            ClientSideSignature = clientSideSig,
            PluginSignature = pluginSig,
            GeoSignature = countryCode,
            IpUaSignature = _hasher.ComputeSignature(ip, userAgent),
            IpClientSignature = ipClientSig,
            UaClientSignature = uaClientSig,
            IpSubnetSignature = !string.IsNullOrEmpty(ip) ? _hasher.HashIpSubnet(ip) : null,
            Timestamp = DateTime.UtcNow,
            FactorCount = factorCount
        };

        _logger.LogDebug(
            "Generated multi-factor signatures with {FactorCount} factors: Primary={Primary}, IP={HasIp}, UA={HasUa}, Client={HasClient}, Plugin={HasPlugin}, Geo={Geo}",
            signatures.FactorCount,
            signatures.PrimarySignature[..Math.Min(8, signatures.PrimarySignature.Length)],
            signatures.IpSignature != null,
            signatures.UaSignature != null,
            signatures.ClientSideSignature != null,
            signatures.PluginSignature != null,
            signatures.GeoSignature ?? "none");

        return signatures;
    }

    private void EvictStaleEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var kvp in _factorCache)
        {
            if (kvp.Value.Timestamp < cutoff)
                _factorCache.TryRemove(kvp.Key, out _);
        }
    }

    private sealed record CachedFactors(
        string? ClientSideSignature,
        string? PluginSignature,
        string? IpClientSignature,
        string? UaClientSignature,
        string? GeoSignature,
        DateTime Timestamp,
        bool IsFromDocumentRequest = false);

    /// <summary>
    ///     Match signatures from current request against stored signatures.
    ///     Returns match confidence (0.0-1.0) and which factors matched.
    /// </summary>
    public SignatureMatchResult MatchSignatures(
        MultiFactorSignatures current,
        MultiFactorSignatures stored)
    {
        var matchedFactors = new List<string>();

        // Check each factor
        if (current.PrimarySignature == stored.PrimarySignature)
            matchedFactors.Add("Primary");

        if (current.IpSignature != null && current.IpSignature == stored.IpSignature)
            matchedFactors.Add("IP");

        if (current.UaSignature != null && current.UaSignature == stored.UaSignature)
            matchedFactors.Add("UA");

        if (current.ClientSideSignature != null && current.ClientSideSignature == stored.ClientSideSignature)
            matchedFactors.Add("ClientSide");

        if (current.PluginSignature != null && current.PluginSignature == stored.PluginSignature)
            matchedFactors.Add("Plugin");

        if (current.IpSubnetSignature != null && current.IpSubnetSignature == stored.IpSubnetSignature)
            matchedFactors.Add("IpSubnet");

        if (current.GeoSignature != null && current.GeoSignature == stored.GeoSignature)
            matchedFactors.Add("Geo");

        // Calculate confidence based on matched factors
        var confidence = CalculateMatchConfidence(matchedFactors, current.FactorCount, stored.FactorCount);

        // Determine match type
        var matchType = DetermineMatchType(matchedFactors);

        return new SignatureMatchResult
        {
            IsMatch = matchedFactors.Count >= _minimumFactorsToMatch,
            Confidence = confidence,
            MatchedFactors = matchedFactors,
            MatchType = matchType,
            FactorsMatched = matchedFactors.Count,
            TotalFactors = Math.Max(current.FactorCount, stored.FactorCount)
        };
    }

    /// <summary>
    ///     Detect non-document requests that should use carry-forward factors.
    ///     WebSocket, fetch/XHR, SignalR negotiate, and other non-HTML requests produce
    ///     different secondary headers (Accept-Encoding, Client Hints) than full page loads.
    /// </summary>
    private static bool IsNonDocumentRequest(HttpContext httpContext)
    {
        // WebSocket upgrade
        if (httpContext.Request.Headers.TryGetValue("Upgrade", out var upgrade)
            && upgrade.ToString().Contains("websocket", StringComparison.OrdinalIgnoreCase))
            return true;

        // Sec-Fetch-Dest tells us exactly what the browser is doing
        var fetchDest = httpContext.Request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (!string.IsNullOrEmpty(fetchDest))
        {
            var dest = fetchDest.ToLowerInvariant();
            // "document" and "iframe" are full page loads with complete headers
            return dest is not ("document" or "iframe");
        }

        // Fallback: XHR/fetch requests typically use specific Accept headers
        var accept = httpContext.Request.Headers.Accept.ToString();
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
            accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    ///     Extract client-side fingerprint from request headers.
    ///     This includes: Canvas hash, WebGL hash, AudioContext hash, Fonts, Plugins, Screen, etc.
    /// </summary>
    private string? ExtractClientFingerprint(HttpContext httpContext)
    {
        // Check for custom fingerprint header (set by client-side JS)
        var fingerprintHeader = httpContext.Request.Headers["X-Client-Fingerprint"].ToString();
        if (!string.IsNullOrEmpty(fingerprintHeader))
            return fingerprintHeader;

        // Build fingerprint from available headers
        var components = new List<string>();

        // Sec-CH-UA headers (Chrome User-Agent Client Hints)
        var secChUa = httpContext.Request.Headers["Sec-CH-UA"].ToString();
        if (!string.IsNullOrEmpty(secChUa))
            components.Add($"ua:{secChUa}");

        var secChUaPlatform = httpContext.Request.Headers["Sec-CH-UA-Platform"].ToString();
        if (!string.IsNullOrEmpty(secChUaPlatform))
            components.Add($"platform:{secChUaPlatform}");

        var secChUaMobile = httpContext.Request.Headers["Sec-CH-UA-Mobile"].ToString();
        if (!string.IsNullOrEmpty(secChUaMobile))
            components.Add($"mobile:{secChUaMobile}");

        // Screen resolution (from custom header if available)
        var screenRes = httpContext.Request.Headers["X-Screen-Resolution"].ToString();
        if (!string.IsNullOrEmpty(screenRes))
            components.Add($"screen:{screenRes}");

        // Timezone offset
        var tz = httpContext.Request.Headers["X-Timezone-Offset"].ToString();
        if (!string.IsNullOrEmpty(tz))
            components.Add($"tz:{tz}");

        if (components.Count == 0)
            return null;

        return string.Join("|", components);
    }

    /// <summary>
    ///     Extract plugin signature from request headers.
    ///     This includes: Installed plugins, extensions, fonts, etc.
    /// </summary>
    private string? ExtractPluginSignature(HttpContext httpContext)
    {
        // Check for custom plugin header (set by client-side JS)
        var pluginHeader = httpContext.Request.Headers["X-Browser-Plugins"].ToString();
        if (!string.IsNullOrEmpty(pluginHeader))
            return pluginHeader;

        var components = new List<string>();

        // Accept-Language (relatively stable)
        var acceptLang = httpContext.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrEmpty(acceptLang))
            components.Add($"lang:{acceptLang}");

        // Accept-Encoding (stable per browser version for same request type,
        // but differs across request types — e.g. page load vs WebSocket vs fetch.
        // The carry-forward logic in GenerateSignatures handles this divergence.)
        var acceptEnc = httpContext.Request.Headers.AcceptEncoding.ToString();
        if (!string.IsNullOrEmpty(acceptEnc))
            components.Add($"enc:{acceptEnc}");

        // DNT header (rare but stable)
        var dnt = httpContext.Request.Headers["DNT"].ToString();
        if (!string.IsNullOrEmpty(dnt))
            components.Add($"dnt:{dnt}");

        if (components.Count == 0)
            return null;

        return string.Join("|", components);
    }

    /// <summary>
    ///     Extract country code from GeoLocation stored by GeoRoutingMiddleware.
    ///     Country code (e.g. "US", "CN") is NOT PII — it's a coarse geographic signal
    ///     more resistant to IP rotation than IP-based signatures.
    /// </summary>
    private static string? ExtractCountryCode(HttpContext httpContext)
    {
        // GeoRoutingMiddleware stores GeoLocation in Items["GeoLocation"]
        if (httpContext.Items.TryGetValue("GeoLocation", out var geoObj) && geoObj != null)
        {
            // Use reflection-free duck typing: the GeoLocation model has a CountryCode property
            var countryProp = geoObj.GetType().GetProperty("CountryCode");
            var countryCode = countryProp?.GetValue(geoObj) as string;
            if (!string.IsNullOrEmpty(countryCode))
                return countryCode;
        }

        return null;
    }

    /// <summary>Count non-null fields in a CachedFactors entry for richness comparison.</summary>
    private static int CountCachedRichness(string? clientSide, string? plugin, string? ipClient, string? uaClient, string? geo)
    {
        var count = 0;
        if (clientSide != null) count++;
        if (plugin != null) count++;
        if (ipClient != null) count++;
        if (uaClient != null) count++;
        if (geo != null) count++;
        return count;
    }

    private double CalculateMatchConfidence(List<string> matchedFactors, int currentFactorCount, int storedFactorCount)
    {
        if (matchedFactors.Count == 0)
            return 0.0;

        // Base confidence on ratio of matched factors
        var maxFactors = Math.Max(currentFactorCount, storedFactorCount);
        var baseConfidence = (double)matchedFactors.Count / maxFactors;

        // Boost confidence if strong factors match
        if (matchedFactors.Contains("Primary"))
            baseConfidence += 0.2;
        if (matchedFactors.Contains("ClientSide"))
            baseConfidence += 0.1;
        if (matchedFactors.Contains("Plugin"))
            baseConfidence += 0.1;

        return Math.Min(1.0, baseConfidence);
    }

    private MatchType DetermineMatchType(List<string> matchedFactors)
    {
        if (matchedFactors.Contains("Primary"))
            return MatchType.Exact;

        if (matchedFactors.Contains("IP") && matchedFactors.Contains("UA"))
            return MatchType.Exact; // Equivalent to primary

        if (matchedFactors.Contains("ClientSide") && matchedFactors.Count >= 2)
            return MatchType.ClientIdentity;

        if (matchedFactors.Contains("IpSubnet") && matchedFactors.Count >= 2)
            return MatchType.NetworkIdentity;

        if (matchedFactors.Contains("Geo") && matchedFactors.Count >= 2)
            return MatchType.GeoIdentity;

        if (matchedFactors.Count >= 2)
            return MatchType.Partial;

        return MatchType.Weak;
    }
}

/// <summary>
///     Multi-factor signatures for a single request.
///     All signatures are HMAC-SHA256 hashes (non-reversible, privacy-safe).
/// </summary>
public sealed class MultiFactorSignatures
{
    /// <summary>Primary signature: HMAC(IP + UA)</summary>
    public string PrimarySignature { get; set; } = "";

    /// <summary>IP signature: HMAC(IP) - for handling UA changes</summary>
    public string? IpSignature { get; set; }

    /// <summary>UA signature: HMAC(UA) - for handling IP changes</summary>
    public string? UaSignature { get; set; }

    /// <summary>Client-side fingerprint: HMAC(Canvas+WebGL+AudioContext+...)</summary>
    public string? ClientSideSignature { get; set; }

    /// <summary>Plugin signature: HMAC(Plugins+Extensions+Fonts)</summary>
    public string? PluginSignature { get; set; }

    /// <summary>IP+UA composite (same as primary for backwards compat)</summary>
    public string? IpUaSignature { get; set; }

    /// <summary>IP+Client composite</summary>
    public string? IpClientSignature { get; set; }

    /// <summary>UA+Client composite</summary>
    public string? UaClientSignature { get; set; }

    /// <summary>IP subnet (/24) signature - for network-level grouping</summary>
    public string? IpSubnetSignature { get; set; }

    /// <summary>Country code (NOT PII) - raw ISO 3166-1 alpha-2 for geo drift detection</summary>
    public string? GeoSignature { get; set; }

    /// <summary>Timestamp when signatures were generated</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Number of factors available (1-4)</summary>
    public int FactorCount { get; set; }
}

/// <summary>
///     Result of matching multi-factor signatures.
/// </summary>
public sealed class SignatureMatchResult
{
    /// <summary>Whether signatures match (requires minimum 2 factors)</summary>
    public bool IsMatch { get; set; }

    /// <summary>Match confidence (0.0-1.0)</summary>
    public double Confidence { get; set; }

    /// <summary>Which factors matched</summary>
    public List<string> MatchedFactors { get; set; } = new();

    /// <summary>Type of match (Exact, Partial, ClientIdentity, etc.)</summary>
    public MatchType MatchType { get; set; }

    /// <summary>Number of factors that matched</summary>
    public int FactorsMatched { get; set; }

    /// <summary>Total number of factors available</summary>
    public int TotalFactors { get; set; }
}

/// <summary>
///     Type of signature match.
/// </summary>
public enum MatchType
{
    /// <summary>No match or too few factors</summary>
    Weak,

    /// <summary>Some factors match (2+) but not all</summary>
    Partial,

    /// <summary>All factors match (exact same client)</summary>
    Exact,

    /// <summary>Client-side identity matches (browser fingerprint stable)</summary>
    ClientIdentity,

    /// <summary>Network identity matches (same network, different client)</summary>
    NetworkIdentity,

    /// <summary>Geographic identity matches (same country, different IP - resistant to IP rotation)</summary>
    GeoIdentity
}