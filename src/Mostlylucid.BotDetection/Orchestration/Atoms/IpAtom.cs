using System.Collections;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    // Datacenter prefix ranges live in the ip.detector.yaml manifest
    // (datacenter_ranges) — read via DetectorConfigProvider so appsettings
    // overrides and the commercial config editor apply. The legacy hardcoded
    // catalog was folded into the manifest as a union (one source of truth,
    // per feedback_no_word_lists).
    private readonly BoundedCache<string, bool> _cidrCache = new(maxSize: 10_000, defaultTtl: TimeSpan.FromHours(1));
    private readonly SemaphoreSlim _cidrLock = new(1, 1);
    private IReadOnlyList<string>? _cachedCidrRanges;
    private DateTime _cidrCacheExpiry = DateTime.MinValue;

    private readonly IAsnLookupService? _asnLookup;
    private readonly IBotListDatabase? _botListDatabase;
    private readonly IBotListFetcher? _botListFetcher;
    private readonly ILogger<IpAtom> _logger;
    private readonly IProxyEnvironment? _proxyEnvironment;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<BotDetectionOptions>? _options;

    public IpAtom(
        ILogger<IpAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        IBotListDatabase? botListDatabase = null,
        IAsnLookupService? asnLookup = null,
        IBotListFetcher? botListFetcher = null,
        IProxyEnvironment? proxyEnvironment = null,
        IOptions<BotDetectionOptions>? options = null)
        : base(name: "Ip", category: "IP")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _botListDatabase = botListDatabase;
        _asnLookup = asnLookup;
        _botListFetcher = botListFetcher;
        _proxyEnvironment = proxyEnvironment;
        _options = options;
    }

    public override int Priority => 12;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double MissingIpPenalty => _configProvider.GetParameter(Name, "missing_ip_penalty", 0.6);
    private double PrivateIpPenalty => _configProvider.GetParameter(Name, "private_ip_penalty", 0.1);
    private double DatacenterConfidence => _configProvider.GetParameter(Name, "datacenter_confidence", 0.6);
    private double IspHumanConfidence => _configProvider.GetParameter(Name, "isp_human_confidence", 0.15);
    private double WeightBase => _configProvider.GetDefaults(Name).Weights.Base;
    private double VpnEgressConfidence => _configProvider.GetParameter(Name, "vpn_egress_confidence", 0.15);

    public override async Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return None();

        // Project the geo/anonymizer signals (geo.country_code, geo.is_vpn, geo.is_proxy,
        // geo.is_tor, geo.is_hosting) from the GeoLocation object GeoRoutingMiddleware stores
        // on HttpContext.Items. Geo is IP-derived, so it lives on the IP atom (Priority 12),
        // which runs before GeoChangeAtom (Priority 16, RequiredSignals=[geo.country_code])
        // and before SignatureCoordinator's geo.is_vpn read. The v8 atom refactor dropped the
        // geo IContributingDetector that used to emit these and never replaced it, so every
        // consumer read false/absent -- this restores the emit. No-op when the geo middleware
        // isn't loaded (the Items entry is absent). geo.* is not IpAtom's owned manifest prefix
        // (ip.*/proxy.*), so the emit-contract test does not police it here.
        EmitGeoSignals(sink, context, sessionId);

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

        // Peer-verified trust for the Internal enforcement carve-out. Computed from the REAL TCP
        // peer (Connection.RemoteIpAddress) + InternalTrust config, never from clientIp (which
        // may be X-Forwarded-For-derived and is therefore spoofable). The Internal classification
        // in DetectionLedgerExtensions reads THIS signal, not ip.is_local, so no header can claim
        // Internal -> logonly and bypass enforcement.
        var isTrustedInternal = InternalTrustEvaluator.IsTrustedInternalPeer(
            context.Connection.RemoteIpAddress,
            _options?.Value.InternalTrust ?? new InternalTrustOptions());
        sink.Raise($"{SignalKeys.IpIsTrustedInternal}:{(isTrustedInternal ? "true" : "false")}", sessionId);

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
            (isDatacenter, datacenterName) = CheckDatacenterPrefix(clientIp, DatacenterRanges());

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

                        // VPN egress ASN check — manifest seeds (vpn_egress_asns)
                        // merged with the free online feed (IBotListFetcher.
                        // GetVpnAsnsAsync, tn3w/IPSet). Catches consumer-VPN
                        // exits (M247/AS9009 etc.) the datacenter lists miss.
                        // Weak contextual prior (vpn_egress_confidence): shapes
                        // rate limiting + sensitive-endpoint escalation, never
                        // dominates the verdict on its own.
                        if (await IsVpnEgressAsnAsync(asnInfo.Asn, ct))
                        {
                            sink.Raise($"{SignalKeys.IpIsVpn}:true", sessionId);
                            sink.Raise($"ip.vpn_asn:{asnInfo.Asn}", sessionId);
                            contributions.Add(new DetectionContribution
                            {
                                DetectorName = Name,
                                Category = Category,
                                ConfidenceDelta = VpnEgressConfidence,
                                Weight = 1.0,
                                Reason = $"VPN egress ASN: {asnInfo.OrgName ?? $"AS{asnInfo.Asn}"} (AS{asnInfo.Asn})"
                            });
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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "GeoLocationSignalEmitter.Emit duck-types HttpContext.Items[\"GeoLocation\"] " +
            "reflectively without a hard reference on Mostlylucid.GeoDetection. Safe in AOT - no-op " +
            "when the geo middleware isn't loaded (the item is absent).")]
    private static void EmitGeoSignals(SignalSink sink, HttpContext context, string sessionId)
        => GeoLocationSignalEmitter.Emit(sink, context, sessionId);

    // Extracted to the shared static ClientIpResolver (Helpers/ClientIpResolver.cs) so other
    // atoms (WebhookSensor) resolve the same CLIENT ip without duplicating this fallback chain.
    private string ResolveClientIp(HttpContext context) => ClientIpResolver.Resolve(context, _proxyEnvironment);

    /// <summary>
    ///     Datacenter prefix ranges from the ip.detector.yaml manifest
    ///     (datacenter_ranges), read through DetectorConfigProvider so
    ///     appsettings overrides and the commercial config editor apply.
    ///     VYaml deserializes the nested vendor→prefixes object as a generic
    ///     IDictionary — parsed defensively here.
    /// </summary>
    private IReadOnlyDictionary<string, string[]> DatacenterRanges()
    {
        var result = new Dictionary<string, string[]>();
        var parameters = _configProvider.GetDefaults(Name).Parameters;
        if (parameters.TryGetValue("datacenter_ranges", out var raw) && raw is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                var vendor = entry.Key?.ToString() ?? "";
                var prefixes = entry.Value switch
                {
                    IEnumerable<string> strs => strs.ToArray(),
                    IEnumerable<object> objs => objs
                        .Select(o => o?.ToString())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => s!)
                        .ToArray(),
                    _ => Array.Empty<string>()
                };
                if (prefixes.Length > 0) result[vendor] = prefixes;
            }
        }
        return result;
    }

    /// <summary>
    ///     True when the ASN is in the manifest vpn_egress_asns seeds OR the
    ///     free online feed (tn3w/IPSet datacenter_asns.json via
    ///     IBotListFetcher, IMemoryCache-cached). A dead feed degrades to
    ///     seeds-only — never throws into the hot path.
    /// </summary>
    private async Task<bool> IsVpnEgressAsnAsync(int asn, CancellationToken ct)
    {
        var parameters = _configProvider.GetDefaults(Name).Parameters;
        if (parameters.TryGetValue("vpn_egress_asns", out var raw) && raw is not null)
            foreach (var seed in FlattenAsns(raw))
                if (seed == asn)
                    return true;

        if (_botListFetcher is not null)
        {
            try
            {
                var feed = await _botListFetcher.GetVpnAsnsAsync(ct);
                return feed.Contains(asn);
            }
            catch
            {
                // Feed unreachable — seeds already checked above.
            }
        }

        return false;
    }

    private static IEnumerable<int> FlattenAsns(object raw)
    {
        if (raw is IEnumerable<object> objs)
        {
            foreach (var o in objs)
                if (int.TryParse(o?.ToString(), out var n))
                    yield return n;
        }
        else if (raw is IEnumerable<int> ints)
        {
            foreach (var n in ints)
                yield return n;
        }
    }

    private static (bool isDatacenter, string? name) CheckDatacenterPrefix(
        string ip, IReadOnlyDictionary<string, string[]> ranges)
    {
        foreach (var (name, prefixes) in ranges)
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
