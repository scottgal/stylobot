using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     ASN (Autonomous System Number) lookup result.
///     Provides accurate datacenter/ISP identification via real routing data.
/// </summary>
public sealed record AsnInfo
{
    /// <summary>ASN number (e.g., 24940 for Hetzner, 8075 for Microsoft/Azure)</summary>
    public int Asn { get; init; }

    /// <summary>CIDR prefix this IP belongs to (e.g., "65.108.0.0/15")</summary>
    public string? Prefix { get; init; }

    /// <summary>Country code from RIR registration</summary>
    public string? CountryCode { get; init; }

    /// <summary>RIR that allocated this range (ripencc, arin, apnic, lacnic, afrinic)</summary>
    public string? Registry { get; init; }

    /// <summary>Organization/AS name (e.g., "HETZNER-AS", "MICROSOFT-CORP-MSN-AS-BLOCK")</summary>
    public string? OrgName { get; init; }

    /// <summary>Whether this ASN is a known datacenter/cloud provider</summary>
    public bool IsDatacenter { get; init; }

    /// <summary>Friendly provider name if identified (e.g., "Hetzner", "Azure", "AWS")</summary>
    public string? ProviderName { get; init; }
}

/// <summary>
///     Service for looking up ASN information using Team Cymru DNS-based queries.
///     This is the standard industry approach for IP→ASN mapping:
///     - Free, no API key required
///     - No rate limits (DNS-based)
///     - Fast (~1-5ms with DNS caching)
///     - Returns real BGP routing data (ASN, prefix, org name)
///
///     Query flow:
///     1. Reverse IP octets → query {reversed}.origin.asn.cymru.com TXT
///        → returns "ASN | prefix | CC | registry | date"
///     2. Query AS{asn}.peer.asn.cymru.com TXT
///        → returns "ASN | CC | registry | date | orgname"
/// </summary>
public interface IAsnLookupService
{
    /// <summary>Look up ASN info for an IP address. Returns null if lookup fails.</summary>
    Task<AsnInfo?> LookupAsync(string ipAddress, CancellationToken ct = default);
}

public class AsnLookupService : IAsnLookupService
{
    // Known datacenter ASNs — maps ASN number to friendly provider name
    private static readonly Dictionary<int, string> KnownDatacenterAsns = new()
    {
        // Major cloud providers
        { 16509, "AWS" },           // Amazon
        { 14618, "AWS" },           // Amazon
        { 8075, "Azure" },          // Microsoft
        { 8068, "Azure" },          // Microsoft
        { 15169, "Google Cloud" },  // Google
        { 396982, "Google Cloud" }, // Google
        { 19527, "Google Cloud" },  // Google

        // European hosting
        { 24940, "Hetzner" },
        { 213230, "Hetzner" },

        // Other major providers
        { 14061, "DigitalOcean" },
        { 63949, "Linode" },        // Akamai Connected Cloud
        { 20473, "Vultr" },
        { 16276, "OVH" },
        { 12876, "Scaleway" },
        { 51167, "Contabo" },
        { 60781, "LeaseWeb" },
        { 24961, "myLoc" },
        { 30633, "Leaseweb" },
        { 36352, "ColoCrossing" },
        { 55286, "B2 Net" },
        { 20860, "IoMart" },
        { 29802, "HVC Data" },
        { 44592, "SkyLink" },
        { 62567, "DigitalOcean" },
        { 131199, "LeaseWeb Asia" },

        // CDN/Infrastructure (not strictly datacenter but worth noting)
        { 13335, "Cloudflare" },
        { 54113, "Fastly" },
        { 20940, "Akamai" },
        { 16625, "Akamai" },

        // VPS providers
        { 46606, "Unified Layer" },
        { 32244, "Liquid Web" },
        { 36351, "SoftLayer" },     // IBM Cloud
        { 19994, "Rackspace" },

        // Russian/Chinese hosting
        { 47541, "VKontakte" },
        { 45090, "Tencent" },
        { 37963, "Alibaba Cloud" },
        { 45102, "Alibaba Cloud" },

        // Oracle
        { 31898, "Oracle Cloud" },
    };

    // Cache: IP → AsnInfo (TTL 1 hour)
    private static readonly ConcurrentDictionary<string, (AsnInfo? Info, DateTime Expiry)> Cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    // Cache: ASN → OrgName (TTL 24 hours, ASN names rarely change)
    private static readonly ConcurrentDictionary<int, (string? OrgName, DateTime Expiry)> OrgCache = new();
    private static readonly TimeSpan OrgCacheTtl = TimeSpan.FromHours(24);

    private readonly ILogger<AsnLookupService> _logger;

    public AsnLookupService(ILogger<AsnLookupService> logger)
    {
        _logger = logger;
    }

    public async Task<AsnInfo?> LookupAsync(string ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return null;

        // Check cache
        if (Cache.TryGetValue(ipAddress, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.Info;

        try
        {
            var info = await LookupCoreAsync(ipAddress, ct);

            // Cache result (including nulls to avoid repeated failures)
            Cache[ipAddress] = (info, DateTime.UtcNow + CacheTtl);

            // Evict old entries periodically
            if (Cache.Count > 50_000)
                EvictExpiredEntries();

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ASN lookup failed for {IP}", ipAddress);
            // Cache the failure briefly (5 min) to avoid hammering DNS
            Cache[ipAddress] = (null, DateTime.UtcNow + TimeSpan.FromMinutes(5));
            return null;
        }
    }

    private async Task<AsnInfo?> LookupCoreAsync(string ipAddress, CancellationToken ct)
    {
        if (!IPAddress.TryParse(ipAddress, out var addr))
            return null;

        // Only support IPv4 for now (IPv6 Team Cymru queries use different format)
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;

        // Step 1: Query origin ASN
        // Reverse IP octets: 65.109.95.231 → 231.95.109.65
        var bytes = addr.GetAddressBytes();
        var reversed = $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}";
        var originQuery = $"{reversed}.origin.asn.cymru.com";

        string? originTxt = await QueryDnsTxt(originQuery, ct);
        if (string.IsNullOrEmpty(originTxt))
            return null;

        // Parse: "24940 | 65.108.0.0/15 | DE | ripencc | 2021-07-14"
        var originParts = originTxt.Split('|', StringSplitOptions.TrimEntries);
        if (originParts.Length < 3)
            return null;

        if (!int.TryParse(originParts[0].Trim(), out var asn))
            return null;

        var prefix = originParts.Length > 1 ? originParts[1].Trim() : null;
        var countryCode = originParts.Length > 2 ? originParts[2].Trim() : null;
        var registry = originParts.Length > 3 ? originParts[3].Trim() : null;

        // Step 2: Get organization name (from cache or DNS)
        var orgName = await GetOrgNameAsync(asn, ct);

        // Step 3: Determine if this is a known datacenter
        var isDatacenter = KnownDatacenterAsns.ContainsKey(asn);
        var providerName = isDatacenter ? KnownDatacenterAsns[asn] : null;

        // If not in our known list but org name contains hosting keywords, flag it
        if (!isDatacenter && !string.IsNullOrEmpty(orgName))
        {
            var orgLower = orgName.ToLowerInvariant();
            if (orgLower.Contains("hosting") || orgLower.Contains("server") ||
                orgLower.Contains("cloud") || orgLower.Contains("datacenter") ||
                orgLower.Contains("data center") || orgLower.Contains("vps") ||
                orgLower.Contains("dedicated") || orgLower.Contains("colocation"))
            {
                isDatacenter = true;
                providerName = ExtractProviderName(orgName);
            }
        }

        return new AsnInfo
        {
            Asn = asn,
            Prefix = prefix,
            CountryCode = countryCode,
            Registry = registry,
            OrgName = orgName,
            IsDatacenter = isDatacenter,
            ProviderName = providerName
        };
    }

    private async Task<string?> GetOrgNameAsync(int asn, CancellationToken ct)
    {
        // Check org cache
        if (OrgCache.TryGetValue(asn, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.OrgName;

        // Query: AS24940.peer.asn.cymru.com TXT
        // Response: "24940 | DE | ripencc | 2003-09-19 | HETZNER-AS, DE"
        var peerQuery = $"AS{asn}.peer.asn.cymru.com";
        var peerTxt = await QueryDnsTxt(peerQuery, ct);

        string? orgName = null;
        if (!string.IsNullOrEmpty(peerTxt))
        {
            var peerParts = peerTxt.Split('|', StringSplitOptions.TrimEntries);
            if (peerParts.Length >= 5)
                orgName = peerParts[4].Trim();
        }

        OrgCache[asn] = (orgName, DateTime.UtcNow + OrgCacheTtl);
        return orgName;
    }

    private static async Task<string?> QueryDnsTxt(string hostname, CancellationToken ct)
    {
        try
        {
            // Use system DNS resolver for TXT record lookup
            var entries = await Dns.GetHostEntryAsync(hostname, ct);

            // GetHostEntryAsync doesn't directly return TXT records.
            // We need to use a raw DNS query approach instead.
            // Fallback: use the aliases field which sometimes contains TXT data
            // Actually, .NET doesn't have built-in TXT record support.
            // Use a manual UDP DNS query.
            return await QueryDnsTxtRaw(hostname, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Raw UDP DNS TXT record query (System.Net.Dns doesn't support TXT records natively).
    /// </summary>
    private static async Task<string?> QueryDnsTxtRaw(string hostname, CancellationToken ct)
    {
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = 3000;

        // Build DNS query packet for TXT record
        var queryId = (ushort)Random.Shared.Next(0, 65536);
        var query = BuildDnsQuery(queryId, hostname, 16); // 16 = TXT record type

        // Send to system DNS resolver (use Google DNS as reliable fallback)
        var dnsServer = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        await udp.SendAsync(query, query.Length, dnsServer);

        // Receive response
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);

        try
        {
            var result = await udp.ReceiveAsync(cts.Token);
            return ParseDnsTxtResponse(queryId, result.Buffer);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static byte[] BuildDnsQuery(ushort id, string hostname, ushort recordType)
    {
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms);

        // Header
        writer.Write(BSwap(id));           // Transaction ID
        writer.Write(BSwap((ushort)0x0100)); // Flags: standard query, recursion desired
        writer.Write(BSwap((ushort)1));    // Questions: 1
        writer.Write(BSwap((ushort)0));    // Answer RRs: 0
        writer.Write(BSwap((ushort)0));    // Authority RRs: 0
        writer.Write(BSwap((ushort)0));    // Additional RRs: 0

        // Question section
        foreach (var label in hostname.Split('.'))
        {
            writer.Write((byte)label.Length);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }
        writer.Write((byte)0); // Root label

        writer.Write(BSwap(recordType));   // Type: TXT (16)
        writer.Write(BSwap((ushort)1));    // Class: IN

        return ms.ToArray();
    }

    private static string? ParseDnsTxtResponse(ushort expectedId, byte[] response)
    {
        if (response.Length < 12) return null;

        // Check transaction ID
        var id = (ushort)((response[0] << 8) | response[1]);
        if (id != expectedId) return null;

        // Check response code (last 4 bits of byte 3)
        var rcode = response[3] & 0x0F;
        if (rcode != 0) return null; // Non-zero = error

        var answerCount = (response[6] << 8) | response[7];
        if (answerCount == 0) return null;

        // Skip question section
        var offset = 12;
        // Skip QNAME
        while (offset < response.Length && response[offset] != 0)
        {
            if ((response[offset] & 0xC0) == 0xC0) { offset += 2; break; }
            offset += response[offset] + 1;
        }
        if (offset < response.Length && response[offset] == 0) offset++;
        offset += 4; // Skip QTYPE + QCLASS

        // Parse first answer
        if (offset >= response.Length) return null;

        // Skip NAME (could be pointer)
        if ((response[offset] & 0xC0) == 0xC0) offset += 2;
        else { while (offset < response.Length && response[offset] != 0) offset += response[offset] + 1; offset++; }

        if (offset + 10 > response.Length) return null;

        // var type = (response[offset] << 8) | response[offset + 1];
        offset += 2; // TYPE
        offset += 2; // CLASS
        offset += 4; // TTL
        var rdLength = (response[offset] << 8) | response[offset + 1];
        offset += 2; // RDLENGTH

        if (offset + rdLength > response.Length) return null;

        // TXT RDATA: one or more <length><text> pairs
        var result = new System.Text.StringBuilder();
        var end = offset + rdLength;
        while (offset < end)
        {
            var txtLen = response[offset++];
            if (offset + txtLen > end) break;
            if (result.Length > 0) result.Append(' ');
            result.Append(System.Text.Encoding.ASCII.GetString(response, offset, txtLen));
            offset += txtLen;
        }

        return result.Length > 0 ? result.ToString() : null;
    }

    private static ushort BSwap(ushort v) => (ushort)((v >> 8) | (v << 8));

    private static string ExtractProviderName(string orgName)
    {
        // Take the first word/token before any comma, dash, or "AS"
        var name = orgName.Split([',', '-'], StringSplitOptions.TrimEntries)[0];
        if (name.EndsWith(" AS", StringComparison.OrdinalIgnoreCase))
            name = name[..^3].Trim();
        return name;
    }

    private void EvictExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expired = Cache.Where(kv => now >= kv.Value.Expiry).Select(kv => kv.Key).Take(1000).ToList();
        foreach (var key in expired) Cache.TryRemove(key, out _);
    }
}
