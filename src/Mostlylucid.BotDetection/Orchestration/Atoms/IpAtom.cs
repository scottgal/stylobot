using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom (per Taxonomy.md) that resolves the real client IP,
///     enriches it with ASN + datacenter classification, and emits the
///     canonical IP-family signals every downstream atom depends on.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>IpContributor</c>. Priority 12 -- Wave 0.
///     </para>
///     <para>
///         Three-layer datacenter classification (parity with the
///         contributor): fast prefix hints, authoritative ASN lookup via
///         <see cref="IAsnLookupService"/>, then dynamic CIDR ranges from
///         <see cref="IBotListDatabase"/>. ASN overrides prefix guesses in
///         both directions.
///     </para>
///     <para>
///         <c>DatacenterPrefixes</c> below is a hardcoded catalog carried
///         over from the legacy contributor -- it duplicates data that
///         belongs in YAML per the no-word-lists rule. Left in place for
///         migration parity; the catalog cleanup is a separate task.
///     </para>
///     <para>
///         CIDR range cache moves from static field to instance state on
///         the atom singleton -- functionally equivalent but testable.
///     </para>
/// </remarks>
public sealed class IpAtom : DetectorAtomBase
{
    // TODO: migrate to YAML per feedback_no_word_lists.
    private static readonly Dictionary<string, string[]> DatacenterPrefixes = new()
    {
        { "AWS", ["3.", "15.", "18.", "54.", "99."] },
        { "Azure", ["20.", "40.", "65.52.", "65.55."] },
        { "Google Cloud", ["34.", "130.", "146."] },
        { "DigitalOcean", ["104.131.", "104.236.", "159.65.", "138.197.", "167.71.", "206.189."] },
        { "Linode", ["45.33.", "45.56.", "50.116.", "69.164.", "72.14.", "96.126."] },
        { "OVH", ["51.38.", "51.68.", "51.77.", "51.83.", "51.89.", "51.91.", "51.161.", "51.178.", "51.195.", "51.210.", "54.37."] },
        { "Vultr", ["45.32.", "45.63.", "45.76.", "45.77.", "66.42.", "78.141.", "95.179.", "104.156.", "108.61.", "136.244.", "140.82.", "149.28.", "207.148.", "209.250."] },
        { "Hetzner", ["5.9.", "23.88.", "46.4.", "65.21.", "65.108.", "65.109.", "78.46.", "78.47.", "85.10.", "88.198.", "88.99.", "95.216.", "116.202.", "116.203.", "128.140.", "135.181.", "136.243.", "138.201.", "142.132.", "144.76.", "148.251.", "157.90.", "159.69.", "162.55.", "167.235.", "168.119.", "176.9.", "178.63.", "195.201.", "213.133.", "213.239."] }
    };

    private readonly BoundedCache<string, bool> _cidrCache = new(maxSize: 10_000, defaultTtl: TimeSpan.FromHours(1));
    private readonly SemaphoreSlim _cidrLock = new(1, 1);
    private IReadOnlyList<string>? _cachedCidrRanges;
    private DateTime _cidrCacheExpiry = DateTime.MinValue;

    private readonly IAsnLookupService? _asnLookup;
    private readonly IBotListDatabase? _botListDatabase;
    private readonly ILogger<IpAtom> _logger;
    private readonly IProxyEnvironment? _proxyEnvironment;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public IpAtom(
        ILogger<IpAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        IBotListDatabase? botListDatabase = null,
        IAsnLookupService? asnLookup = null,
        IProxyEnvironment? proxyEnvironment = null)
        : base(name: "Ip", category: "IP")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _botListDatabase = botListDatabase;
        _asnLookup = asnLookup;
        _proxyEnvironment = proxyEnvironment;
    }

    public override int Priority => 12;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double MissingIpPenalty => _configProvider.GetParameter(Name, "missing_ip_penalty", 0.6);
    private double PrivateIpPenalty => _configProvider.GetParameter(Name, "private_ip_penalty", 0.1);
    private double DatacenterConfidence => _configProvider.GetParameter(Name, "datacenter_confidence", 0.6);
    private double IspHumanConfidence => _configProvider.GetParameter(Name, "isp_human_confidence", 0.15);
    private double WeightBase => _configProvider.GetDefaults(Name).Weights.Base;

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        sink.Raise("ip.ran", sessionId);

        var contributions = new List<DetectionContribution>();
        var clientIp = ResolveClientIp(context);

        // ClientIp is the canonical IP hint every downstream atom keys off --
        // it was already sink-resident under the legacy contributor. Parity
        // preserved (raw IP is PII but this contract predates the state-vs-
        // signal rule; migrating it out requires downstream refactors).
        sink.Raise($"{SignalKeys.ClientIp}:{clientIp}", sessionId);
        sink.Raise($"{SignalKeys.ProxyTopology}:{_proxyEnvironment?.DetectedTopology.ToString() ?? "Unknown"}", sessionId);

        if (string.IsNullOrEmpty(clientIp))
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = MissingIpPenalty,
                Weight = 1.0,
                Reason = "Missing client IP address",
                BotType = BotType.Unknown.ToString()
            });
            return contributions;
        }

        var isLocal = NetworkHelper.IsLocalIp(clientIp);
        var isLoopback = clientIp is "::1" or "127.0.0.1" or "localhost";
        sink.Raise($"{SignalKeys.IpIsLocal}:{(isLocal ? "true" : "false")}", sessionId);

        if (isLocal)
        {
            var hasProxyHeaders = context.Request.Headers.ContainsKey("X-Forwarded-For")
                                  || context.Request.Headers.ContainsKey("X-Real-IP")
                                  || context.Request.Headers.ContainsKey("Forwarded");
            var isProxied = !isLoopback && hasProxyHeaders;

            if (isLoopback && !hasProxyHeaders)
            {
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = -IspHumanConfidence,
                    Weight = 1.0,
                    Reason = $"Loopback address: {PrivacyHelper.MaskIp(clientIp)} - direct local connection"
                });
            }
            else
            {
                var penalty = isProxied ? 0.0 : PrivateIpPenalty;
                contributions.Add(new DetectionContribution
                {
                    DetectorName = Name,
                    Category = Category,
                    ConfidenceDelta = penalty,
                    Weight = WeightBase * 0.5,
                    Reason = isProxied
                        ? $"Behind reverse proxy: {PrivacyHelper.MaskIp(clientIp)} (proxy headers present)"
                        : $"Private network IP: {PrivacyHelper.MaskIp(clientIp)}"
                });
            }
        }

        var isDatacenter = false;
        string? datacenterName = null;
        int? asn = null;

        if (!isLocal)
        {
            (isDatacenter, datacenterName) = CheckDatacenterPrefix(clientIp);

            if (_asnLookup is not null)
            {
                try
                {
                    var asnInfo = await _asnLookup.LookupAsync(clientIp, ct).ConfigureAwait(false);
                    if (asnInfo is not null)
                    {
                        asn = asnInfo.Asn;
                        sink.Raise($"ip.asn:{asnInfo.Asn}", sessionId);
                        if (!string.IsNullOrEmpty(asnInfo.OrgName))
                            sink.Raise($"ip.asn_org:{asnInfo.OrgName}", sessionId);

                        if (asnInfo.IsDatacenter)
                        {
                            isDatacenter = true;
                            datacenterName = asnInfo.ProviderName ?? asnInfo.OrgName ?? datacenterName;
                        }
                        else if (isDatacenter)
                        {
                            _logger.LogDebug(
                                "Prefix matched {Provider} but ASN {Asn} ({Org}) is not a known datacenter - downgrading",
                                datacenterName, asnInfo.Asn, asnInfo.OrgName);
                            isDatacenter = false;
                            datacenterName = null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ASN lookup failed for {IP}, falling back to prefix/CIDR",
                        PrivacyHelper.MaskIp(clientIp));
                }
            }

            if (!isDatacenter && _botListDatabase is not null)
            {
                try
                {
                    isDatacenter = await CheckDynamicCidrRangesAsync(clientIp, ct).ConfigureAwait(false);
                    if (isDatacenter) datacenterName ??= "Cloud Provider";
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dynamic CIDR range check failed for {IP}", PrivacyHelper.MaskIp(clientIp));
                }
            }
        }

        sink.Raise($"{SignalKeys.IpIsDatacenter}:{(isDatacenter ? "true" : "false")}", sessionId);
        if (!string.IsNullOrEmpty(datacenterName))
            sink.Raise($"{SignalKeys.IpProvider}:{datacenterName}", sessionId);

        if (isDatacenter)
        {
            sink.Raise($"ip.datacenter_name:{datacenterName!}", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = DatacenterConfidence,
                Weight = 1.2,
                Reason = $"Datacenter IP detected: {datacenterName}" + (asn.HasValue ? $" (AS{asn})" : ""),
                BotType = BotType.Unknown.ToString()
            });
        }

        if (!isDatacenter && !isLocal && asn is > 0)
        {
            var orgName = sink.ReadHint("ip.asn_org") ?? "Unknown ISP";
            sink.Raise($"{SignalKeys.IpIsIsp}:true", sessionId);
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -IspHumanConfidence,
                Weight = 1.0,
                Reason = $"ISP/residential: {orgName} (AS{asn})"
            });
        }

        var isIpv6 = clientIp.Contains(':');
        sink.Raise($"{SignalKeys.IpIsIpv6}:{(isIpv6 ? "true" : "false")}", sessionId);

        if (contributions.Count == 0)
        {
            contributions.Add(new DetectionContribution
            {
                DetectorName = Name,
                Category = Category,
                ConfidenceDelta = -IspHumanConfidence,
                Weight = 1.0,
                Reason = $"IP appears normal: {PrivacyHelper.MaskIp(clientIp)}"
            });
        }

        return contributions;
    }

    private async Task<bool> CheckDynamicCidrRangesAsync(string ip, CancellationToken ct)
    {
        if (_cidrCache.TryGet(ip, out var cached)) return cached;

        var ranges = await GetCachedCidrRangesAsync(ct).ConfigureAwait(false);
        if (ranges is null || ranges.Count == 0) return false;

        var result = false;
        if (IPAddress.TryParse(ip, out var addr))
        {
            foreach (var cidr in ranges)
            {
                if (CidrHelper.IsInSubnet(addr, cidr))
                {
                    result = true;
                    break;
                }
            }
        }

        _cidrCache.Set(ip, result);
        return result;
    }

    private async Task<IReadOnlyList<string>?> GetCachedCidrRangesAsync(CancellationToken ct)
    {
        if (_cachedCidrRanges is not null && DateTime.UtcNow < _cidrCacheExpiry)
            return _cachedCidrRanges;

        await _cidrLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedCidrRanges is not null && DateTime.UtcNow < _cidrCacheExpiry)
                return _cachedCidrRanges;

            var ranges = await _botListDatabase!.GetDatacenterIpRangesAsync(ct).ConfigureAwait(false);
            _cachedCidrRanges = ranges;
            _cidrCacheExpiry = DateTime.UtcNow.AddMinutes(5);
            _cidrCache.Clear();

            if (ranges.Count > 0)
                _logger.LogDebug("Loaded {Count} dynamic CIDR ranges for datacenter detection", ranges.Count);

            return ranges;
        }
        finally
        {
            _cidrLock.Release();
        }
    }

    private string ResolveClientIp(HttpContext context)
    {
        if (_proxyEnvironment is not null)
            return _proxyEnvironment.GetRealClientIp(context);

        var connectionIp = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        if (!string.IsNullOrEmpty(connectionIp) && !NetworkHelper.IsLocalIp(connectionIp))
            return connectionIp;

        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            var firstIp = forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrEmpty(firstIp) && !NetworkHelper.IsLocalIp(firstIp))
                return firstIp;
        }

        return connectionIp;
    }

    private static (bool isDatacenter, string? name) CheckDatacenterPrefix(string ip)
    {
        foreach (var (name, prefixes) in DatacenterPrefixes)
        {
            foreach (var prefix in prefixes)
            {
                if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return (true, name);
            }
        }
        return (false, null);
    }
}
