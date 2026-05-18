using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel.Providers;

/// <summary>
///     Aggregated cloud-provider IP-range lookup. Returns <c>cloud:&lt;vendor&gt;</c>
///     classifications so downstream consumers can distinguish vendor identification
///     from generic threat-intel.
///
///     <para>For AWS / GCP / Azure / Cloudflare the provider <em>consumes</em>
///     <see cref="IBotListFetcher.GetDatacenterIpRangesByVendorAsync"/> rather than
///     re-fetching the upstream URLs - those are already pulled by
///     <c>BotListUpdateService</c> for the <c>request.ip.is_datacenter</c> signal,
///     and the duplicate fetch would have served no purpose. Fastly is the one
///     vendor BotListFetcher doesn't cover, so this provider fetches it directly.</para>
///
///     <para>To change URLs / disable individual vendors for the four shared
///     sources, edit <c>BotDetection:BotPatterns:DataSources:*</c> - those flags
///     govern both BotListFetcher's existing detection use and this provider's
///     classification use.</para>
/// </summary>
internal sealed class CloudRangesProvider : IThreatIntelProvider
{
    private readonly HttpClient _http;
    private readonly IBotListFetcher _botList;
    private readonly CloudRangesOptions _options;
    private readonly ILogger<CloudRangesProvider> _logger;

    private volatile List<(string Vendor, IpCidrCache Cache)>? _caches;
    private DateTime _lastRefreshUtc;

    public CloudRangesProvider(
        HttpClient http,
        IBotListFetcher botList,
        IOptions<BotDetectionOptions> options,
        ILogger<CloudRangesProvider> logger)
    {
        _http = http;
        _botList = botList;
        _options = options.Value.ThreatIntel.Providers.CloudRanges;
        _logger = logger;
    }

    public string Name => "cloud-ranges";
    public ThreatIntelMode Mode => ThreatIntelMode.Offline;
    public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
    public TimeSpan RefreshInterval => TimeSpan.FromHours(_options.RefreshHours);

    public ProviderStatus GetStatus()
    {
        var caches = _caches;
        var total = caches?.Sum(c => c.Cache.Count) ?? 0;
        return new ProviderStatus
        {
            Provider = Name,
            Mode = Mode,
            Enabled = _options.Enabled,
            CacheSize = total,
            LastRefreshUtc = _lastRefreshUtc == default ? null : _lastRefreshUtc,
            RefreshInterval = RefreshInterval
        };
    }

    public ThreatIntelVerdict? TryLookup(ThreatSubject subject)
    {
        if (subject.Type != ThreatSubjectType.Ip) return null;
        var caches = _caches;
        if (caches is null) return null;
        if (!IPAddress.TryParse(subject.Value, out var ip)) return null;
        foreach (var (vendor, cache) in caches)
        {
            if (cache.Contains(ip))
            {
                return new ThreatIntelVerdict
                {
                    Provider = Name,
                    Classification = $"cloud:{vendor}",
                    Confidence = 0.6,
                    IntelligenceClass = IntelligenceSignalClass.CloudInfrastructure,
                    ObservedUtc = _lastRefreshUtc,
                    ExpiresUtc = default,
                    Metadata = new Dictionary<string, string> { ["vendor"] = vendor }
                };
            }
        }
        return null;
    }

    public async Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
    {
        var next = new List<(string, IpCidrCache)>(5);

        // 1. AWS / GCP / Azure / Cloudflare: read from BotListFetcher's cache.
        // BotListUpdateService is already pulling these on its own schedule;
        // re-fetching here would burn bandwidth for no extra coverage.
        try
        {
            var byVendor = await _botList.GetDatacenterIpRangesByVendorAsync(cancellationToken);
            foreach (var vendor in (string[])["aws", "gcp", "azure", "cloudflare"])
            {
                if (!IncludeVendor(vendor)) continue;
                if (byVendor.TryGetValue(vendor, out var cidrs) && cidrs.Count > 0)
                {
                    next.Add((vendor, new IpCidrCache(cidrs)));
                    _logger.LogInformation("{Provider}: loaded {Count} ranges for {Vendor} (via BotListFetcher)",
                        Name, cidrs.Count, vendor);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "{Provider}: BotListFetcher fetch failed; preserving any prior cloud-range caches",
                Name);
            // Preserve prior per-vendor caches across all four shared vendors so a single
            // bad fetch from BotListFetcher doesn't blank the whole classification surface.
            foreach (var vendor in (string[])["aws", "gcp", "azure", "cloudflare"])
            {
                var prior = _caches?.FirstOrDefault(c => c.Vendor == vendor);
                if (prior?.Cache is not null) next.Add(prior.Value);
            }
        }

        // 2. Fastly: dedicated fetch (BotListFetcher doesn't cover it).
        if (_options.Fastly.Enabled && !string.IsNullOrEmpty(_options.Fastly.Url))
        {
            try
            {
                var body = await _http.GetStringAsync(_options.Fastly.Url, cancellationToken);
                var cidrs = ParseFastlyJson(body).ToList();
                next.Add(("fastly", new IpCidrCache(cidrs)));
                _logger.LogInformation("{Provider}: loaded {Count} ranges for fastly", Name, cidrs.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Provider}: fastly fetch failed; preserving any prior fastly cache", Name);
                var prior = _caches?.FirstOrDefault(c => c.Vendor == "fastly");
                if (prior?.Cache is not null) next.Add(prior.Value);
            }
        }

        _caches = next;
        _lastRefreshUtc = DateTime.UtcNow;
    }

    private bool IncludeVendor(string vendor) => vendor switch
    {
        "aws"        => _options.IncludeAws,
        "gcp"        => _options.IncludeGcp,
        "azure"      => _options.IncludeAzure,
        "cloudflare" => _options.IncludeCloudflare,
        _            => false
    };

    /// <summary>
    ///     Fastly's public-ip-list endpoint. <c>{ addresses: [v4...], ipv6_addresses: [v6...] }</c>.
    ///     Internal so the parser-tests can exercise it without spinning up the provider.
    /// </summary>
    internal static IEnumerable<string> ParseFastlyJson(string body)
    {
        var doc = JsonSerializer.Deserialize(body, CloudRangesJsonContext.Default.FastlyRanges);
        if (doc?.Addresses is not null) foreach (var a in doc.Addresses) yield return a;
        if (doc?.Ipv6Addresses is not null) foreach (var a in doc.Ipv6Addresses) yield return a;
    }

    internal sealed class FastlyRanges
    {
        [JsonPropertyName("addresses")] public List<string>? Addresses { get; set; }
        [JsonPropertyName("ipv6_addresses")] public List<string>? Ipv6Addresses { get; set; }
    }
}

[JsonSerializable(typeof(CloudRangesProvider.FastlyRanges))]
internal partial class CloudRangesJsonContext : JsonSerializerContext;

/// <summary>
///     Config for the cloud-ranges provider. AWS / GCP / Azure / Cloudflare are
///     pulled via <see cref="IBotListFetcher"/> using its own URL config (under
///     <c>BotDetection:BotPatterns:DataSources</c>) so a single fetch covers both
///     the existing datacenter-IP signal and this provider's classification.
///     The Include* flags only control whether the result is exposed as a
///     <c>cloud:&lt;vendor&gt;</c> classification by this provider - they don't
///     affect the underlying fetch.
/// </summary>
public sealed class CloudRangesOptions
{
    /// <summary>Master enable flag for the provider as a whole. FOSS default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to refresh. Stagger applies on top of this.</summary>
    public double RefreshHours { get; set; } = 24;

    /// <summary>Surface AWS ranges as <c>cloud:aws</c>. Data comes from BotListFetcher.</summary>
    public bool IncludeAws { get; set; } = true;

    /// <summary>Surface Azure ranges as <c>cloud:azure</c>. Data comes from BotListFetcher.</summary>
    public bool IncludeAzure { get; set; } = true;

    /// <summary>Surface GCP ranges as <c>cloud:gcp</c>. Data comes from BotListFetcher.</summary>
    public bool IncludeGcp { get; set; } = true;

    /// <summary>Surface Cloudflare ranges as <c>cloud:cloudflare</c>. Data comes from BotListFetcher.</summary>
    public bool IncludeCloudflare { get; set; } = true;

    /// <summary>Fastly's public-ip-list endpoint. BotListFetcher doesn't cover Fastly, so this is fetched directly.</summary>
    public FastlyRangesSource Fastly { get; set; } = new();
}

/// <summary>Per-vendor source config. Currently only Fastly needs its own URL (the others go through BotListFetcher).</summary>
public sealed class FastlyRangesSource
{
    public bool Enabled { get; set; } = true;
    public string Url { get; set; } = "https://api.fastly.com/public-ip-list";
}
