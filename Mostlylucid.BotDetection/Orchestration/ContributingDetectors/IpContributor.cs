using System.Net;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     IP address analysis for bot detection.
///     Runs in the first wave (no dependencies).
///     Analyzes client IP for bot indicators.
///
///     Configuration loaded from: ip.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:IpContributor:*
/// </summary>
public class IpContributor : ConfiguredContributorBase
{
    // Local/private IP ranges (includes "localhost" as a string)
    private static readonly string[] LocalPrefixes =
    [
        "127.", "10.", "172.16.", "172.17.", "172.18.", "172.19.", "172.20.", "172.21.",
        "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", "172.27.", "172.28.",
        "172.29.", "172.30.", "172.31.", "192.168.", "::1", "fe80:", "localhost"
    ];

    // Known datacenter IP ranges (sample - in production use a dedicated service)
    private static readonly Dictionary<string, string[]> DatacenterRanges = new()
    {
        { "AWS", ["3.", "13.", "15.", "18.", "35.", "52.", "54.", "99."] },
        { "Azure", ["13.", "20.", "23.", "40.", "51.", "52.", "65.", "104."] },
        { "Google Cloud", ["34.", "35.", "104.", "130.", "142.", "146.", "162."] },
        { "DigitalOcean", ["104.131.", "104.236.", "159.65.", "138.197.", "167.71.", "206.189."] },
        { "Linode", ["45.33.", "45.56.", "50.116.", "69.164.", "72.14.", "96.126."] },
        { "OVH", ["51.38.", "51.68.", "51.77.", "51.83.", "51.89.", "51.91.", "51.161.", "51.178.", "51.195.", "51.210.", "54.37."] },
        { "Vultr", ["45.32.", "45.63.", "45.76.", "45.77.", "66.42.", "78.141.", "95.179.", "104.156.", "108.61.", "136.244.", "140.82.", "149.28.", "207.148.", "209.250."] },
        { "Hetzner", ["5.9.", "23.88.", "46.4.", "78.46.", "78.47.", "85.10.", "88.198.", "88.99.", "95.216.", "116.202.", "116.203.", "128.140.", "135.181.", "136.243.", "138.201.", "142.132.", "144.76.", "148.251.", "157.90.", "159.69.", "162.55.", "167.235.", "168.119.", "176.9.", "178.63.", "195.201.", "213.133.", "213.239."] }
    };

    private readonly ILogger<IpContributor> _logger;

    public IpContributor(
        ILogger<IpContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "Ip";
    public override int Priority => Manifest?.Priority ?? 12;

    // No triggers - runs in first wave
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters (from YAML defaults.parameters)
    private double MissingIpPenalty => GetParam("missing_ip_penalty", 0.6);
    private double LoopbackPenalty => GetParam("loopback_penalty", 0.0);
    private double PrivateIpPenalty => GetParam("private_ip_penalty", 0.1);
    private double DatacenterConfidence => GetParam("datacenter_confidence", 0.6);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        var contributions = new List<DetectionContribution>();
        var clientIp = ResolveClientIp(state.HttpContext);

        state.WriteSignal(SignalKeys.ClientIp, clientIp);

        // Check if IP is empty/null - confidence from YAML
        if (string.IsNullOrEmpty(clientIp))
        {
            contributions.Add(BotContribution(
                    "IP",
                    "Missing client IP address",
                    confidenceOverride: MissingIpPenalty,
                    botType: BotType.Unknown.ToString()));
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
        }

        // Check for localhost/local network
        var isLocal = IsLocalIp(clientIp);
        var isLoopback = clientIp is "::1" or "127.0.0.1" or "localhost";
        state.WriteSignal(SignalKeys.IpIsLocal, isLocal);

        if (isLocal)
        {
            // Loopback/private penalties from YAML config
            var penalty = isLoopback ? LoopbackPenalty : PrivateIpPenalty;
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = "IP",
                ConfidenceDelta = penalty,
                Weight = WeightBase * 0.5,
                Reason = isLoopback
                    ? $"Localhost/loopback address: {MaskIp(clientIp)} (neutral in dev)"
                    : $"Private network IP: {MaskIp(clientIp)}"
            });
        }

        // Check for datacenter IP (skip if already identified as local/loopback)
        var isDatacenter = false;
        string? datacenterName = null;
        if (!isLocal) (isDatacenter, datacenterName) = CheckDatacenterIp(clientIp);
        state.WriteSignal(SignalKeys.IpIsDatacenter, isDatacenter);

        if (isDatacenter)
        {
            state.WriteSignal("ip.datacenter_name", datacenterName!);
            contributions.Add(BotContribution(
                    "IP",
                    $"Datacenter IP detected: {datacenterName}",
                    confidenceOverride: DatacenterConfidence, // from YAML
                    weightMultiplier: 1.2,
                    botType: BotType.Unknown.ToString()));
        }

        // Check for IPv6 (less common for bots currently, but this varies)
        var isIpv6 = clientIp.Contains(':');
        state.WriteSignal("ip.is_ipv6", isIpv6);

        // No bot indicators found - use config-driven human indicator
        if (contributions.Count == 0)
            contributions.Add(HumanContribution(
                "IP",
                $"IP appears normal: {MaskIp(clientIp)}"));

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(contributions);
    }

    /// <summary>
    ///     Resolves the real client IP address.
    ///     Prefers Connection.RemoteIpAddress (set by UseForwardedHeaders middleware).
    ///     Falls back to X-Forwarded-For header when the connection IP is a private/Docker network IP,
    ///     which indicates UseForwardedHeaders isn't configured or doesn't trust the proxy.
    /// </summary>
    private static string ResolveClientIp(Microsoft.AspNetCore.Http.HttpContext httpContext)
    {
        var connectionIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "";

        // If connection IP is already public, UseForwardedHeaders did its job — use it
        if (!string.IsNullOrEmpty(connectionIp) && !IsLocalIp(connectionIp))
            return connectionIp;

        // Connection IP is private (likely Docker/proxy). Check X-Forwarded-For as fallback.
        var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // X-Forwarded-For format: "client, proxy1, proxy2" — leftmost is the original client
            var firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrEmpty(firstIp) && !IsLocalIp(firstIp))
                return firstIp;
        }

        // No forwarded header or it's also private — return what we have
        return connectionIp;
    }

    private static bool IsLocalIp(string ip)
    {
        // Prefix check for common IPv4 ranges (fast path)
        foreach (var prefix in LocalPrefixes)
            if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        // Full IPAddress parsing for accurate IPv4/IPv6 checks
        if (IPAddress.TryParse(ip, out var addr))
        {
            if (IPAddress.IsLoopback(addr))
                return true;

            // IPv6 link-local (fe80::/10)
            if (addr.IsIPv6LinkLocal)
                return true;

            // IPv6 site-local (fec0::/10 — deprecated but still used)
            if (addr.IsIPv6SiteLocal)
                return true;

            // IPv6 unique local address (fc00::/7 — ULA, equivalent to RFC 1918)
            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var bytes = addr.GetAddressBytes();
                if ((bytes[0] & 0xFE) == 0xFC) return true;
            }

            // IPv4-mapped IPv6 (::ffff:10.x.x.x etc.)
            if (addr.IsIPv4MappedToIPv6)
            {
                var mapped = addr.MapToIPv4();
                return IsLocalIp(mapped.ToString());
            }
        }

        return false;
    }

    private static (bool isDatacenter, string? name) CheckDatacenterIp(string ip)
    {
        foreach (var (name, prefixes) in DatacenterRanges)
        foreach (var prefix in prefixes)
            if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (true, name);

        return (false, null);
    }

    private static string MaskIp(string ip)
    {
        // Mask last octet for privacy in logs
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";

        // For IPv6, truncate
        if (ip.Length > 10)
            return ip[..10] + "...";

        return ip;
    }
}