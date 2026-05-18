using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel.Providers;

/// <summary>
///     Aggregated cloud-provider IP-range lookup. One provider, per-vendor
///     <see cref="CloudRangesOptions.Sources"/> config; classification returned
///     as <c>cloud:&lt;vendor&gt;</c> (e.g. <c>cloud:aws</c>) so downstream
///     consumers can distinguish without parsing the metadata.
///
///     <para>Each source has its own URL + parser format - operators running an
///     internal mirror can re-point any single vendor without touching the
///     others. Format dispatch is per-source so adding a new cloud (say
///     <c>oracle-json</c>) is a small parser-handler addition.</para>
/// </summary>
internal sealed class CloudRangesProvider : IThreatIntelProvider
{
    private readonly HttpClient _http;
    private readonly CloudRangesOptions _options;
    private readonly ILogger<CloudRangesProvider> _logger;

    // Per-vendor caches. Classification is determined by which vendor's cache
    // produced the hit, so we can't collapse this into a single shared cache.
    private volatile List<(string Vendor, IpCidrCache Cache)>? _caches;
    private DateTime _lastRefreshUtc;

    public CloudRangesProvider(
        HttpClient http,
        IOptions<BotDetectionOptions> options,
        ILogger<CloudRangesProvider> logger)
    {
        _http = http;
        _options = options.Value.ThreatIntel.Providers.CloudRanges;
        _logger = logger;
    }

    public string Name => "cloud-ranges";
    public ThreatIntelMode Mode => ThreatIntelMode.Offline;
    public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
    public TimeSpan RefreshInterval => TimeSpan.FromHours(_options.RefreshHours);

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
                    Confidence = 0.6,                       // identification, not maliciousness
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
        var next = new List<(string, IpCidrCache)>(_options.Sources.Count);
        foreach (var (vendor, source) in _options.Sources)
        {
            if (!source.Enabled || string.IsNullOrEmpty(source.Url)) continue;
            try
            {
                var body = await _http.GetStringAsync(source.Url, cancellationToken);
                var cidrs = ParseByFormat(source.Format, body).ToList();
                next.Add((vendor, new IpCidrCache(cidrs)));
                _logger.LogInformation("{Provider}: loaded {Count} ranges for {Vendor} ({Format})",
                    Name, cidrs.Count, vendor, source.Format);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "{Provider}: refresh failed for {Vendor} ({Url}); keeping any previous cache for this vendor",
                    Name, vendor, source.Url);
                // Preserve the prior entry for this vendor so a single flaky upstream
                // doesn't blank the whole vendor's lookup until next successful tick.
                var prior = _caches?.FirstOrDefault(c => c.Vendor == vendor);
                if (prior?.Cache is not null) next.Add(prior.Value);
            }
        }
        _caches = next;
        _lastRefreshUtc = DateTime.UtcNow;
    }

    /// <summary>
    ///     Dispatches to the right per-vendor parser. Adding a new cloud vendor =
    ///     one new case + one new parser method.
    /// </summary>
    internal static IEnumerable<string> ParseByFormat(string format, string body) => format switch
    {
        "aws-json"     => ParseAwsJson(body),
        "azure-json"   => ParseAzureJson(body),
        "gcp-json"     => ParseGcpJson(body),
        "cidr-text"    => ParseCidrText(body),
        "fastly-json"  => ParseFastlyJson(body),
        _              => throw new NotSupportedException($"Unknown cloud-ranges format: {format}")
    };

    internal static IEnumerable<string> ParseAwsJson(string body)
    {
        var doc = JsonSerializer.Deserialize(body, CloudRangesJsonContext.Default.AwsRanges);
        if (doc?.Prefixes is not null)
            foreach (var p in doc.Prefixes) if (!string.IsNullOrEmpty(p.IpPrefix)) yield return p.IpPrefix;
        if (doc?.Ipv6Prefixes is not null)
            foreach (var p in doc.Ipv6Prefixes) if (!string.IsNullOrEmpty(p.Ipv6Prefix)) yield return p.Ipv6Prefix;
    }

    internal static IEnumerable<string> ParseAzureJson(string body)
    {
        var doc = JsonSerializer.Deserialize(body, CloudRangesJsonContext.Default.AzureServiceTags);
        if (doc?.Values is null) yield break;
        foreach (var tag in doc.Values)
        {
            if (tag.Properties?.AddressPrefixes is null) continue;
            foreach (var p in tag.Properties.AddressPrefixes) yield return p;
        }
    }

    internal static IEnumerable<string> ParseGcpJson(string body)
    {
        var doc = JsonSerializer.Deserialize(body, CloudRangesJsonContext.Default.GcpRanges);
        if (doc?.Prefixes is null) yield break;
        foreach (var p in doc.Prefixes)
        {
            if (!string.IsNullOrEmpty(p.Ipv4Prefix)) yield return p.Ipv4Prefix;
            if (!string.IsNullOrEmpty(p.Ipv6Prefix)) yield return p.Ipv6Prefix;
        }
    }

    internal static IEnumerable<string> ParseFastlyJson(string body)
    {
        var doc = JsonSerializer.Deserialize(body, CloudRangesJsonContext.Default.FastlyRanges);
        if (doc?.Addresses is not null) foreach (var a in doc.Addresses) yield return a;
        if (doc?.Ipv6Addresses is not null) foreach (var a in doc.Ipv6Addresses) yield return a;
    }

    internal static IEnumerable<string> ParseCidrText(string body)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            yield return line;
        }
    }

    // === Per-vendor DTOs ===

    internal sealed class AwsRanges
    {
        [JsonPropertyName("prefixes")] public List<AwsPrefix>? Prefixes { get; set; }
        [JsonPropertyName("ipv6_prefixes")] public List<AwsIpv6Prefix>? Ipv6Prefixes { get; set; }
    }
    internal sealed class AwsPrefix { [JsonPropertyName("ip_prefix")] public string? IpPrefix { get; set; } }
    internal sealed class AwsIpv6Prefix { [JsonPropertyName("ipv6_prefix")] public string? Ipv6Prefix { get; set; } }

    internal sealed class AzureServiceTags
    {
        [JsonPropertyName("values")] public List<AzureTag>? Values { get; set; }
    }
    internal sealed class AzureTag
    {
        [JsonPropertyName("properties")] public AzureTagProperties? Properties { get; set; }
    }
    internal sealed class AzureTagProperties
    {
        [JsonPropertyName("addressPrefixes")] public List<string>? AddressPrefixes { get; set; }
    }

    internal sealed class GcpRanges
    {
        [JsonPropertyName("prefixes")] public List<GcpPrefix>? Prefixes { get; set; }
    }
    internal sealed class GcpPrefix
    {
        [JsonPropertyName("ipv4Prefix")] public string? Ipv4Prefix { get; set; }
        [JsonPropertyName("ipv6Prefix")] public string? Ipv6Prefix { get; set; }
    }

    internal sealed class FastlyRanges
    {
        [JsonPropertyName("addresses")] public List<string>? Addresses { get; set; }
        [JsonPropertyName("ipv6_addresses")] public List<string>? Ipv6Addresses { get; set; }
    }
}

[JsonSerializable(typeof(CloudRangesProvider.AwsRanges))]
[JsonSerializable(typeof(CloudRangesProvider.AzureServiceTags))]
[JsonSerializable(typeof(CloudRangesProvider.GcpRanges))]
[JsonSerializable(typeof(CloudRangesProvider.FastlyRanges))]
internal partial class CloudRangesJsonContext : JsonSerializerContext;

/// <summary>Per-vendor config for the cloud-ranges provider.</summary>
public sealed class CloudRangesOptions
{
    /// <summary>Master enable flag for the provider as a whole. FOSS default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often to refresh ALL sources. The orchestrator stagger applies on top of this.</summary>
    public double RefreshHours { get; set; } = 24;

    /// <summary>Per-vendor source configurations. Keyed on vendor name (aws/azure/gcp/cloudflare/fastly/...).</summary>
    public Dictionary<string, CloudRangesSource> Sources { get; set; } = new()
    {
        ["aws"]        = new CloudRangesSource { Url = "https://ip-ranges.amazonaws.com/ip-ranges.json",                    Format = "aws-json" },
        ["azure"]      = new CloudRangesSource { Url = "https://download.microsoft.com/download/7/1/D/71D86715-5596-4529-9B13-DA13A5DE5B63/ServiceTags_Public_20260518.json", Format = "azure-json" },
        ["gcp"]        = new CloudRangesSource { Url = "https://www.gstatic.com/ipranges/cloud.json",                       Format = "gcp-json" },
        ["cloudflare"] = new CloudRangesSource { Url = "https://www.cloudflare.com/ips-v4",                                 Format = "cidr-text" },
        ["fastly"]     = new CloudRangesSource { Url = "https://api.fastly.com/public-ip-list",                             Format = "fastly-json" }
    };
}

/// <summary>One vendor's source config inside <see cref="CloudRangesOptions.Sources"/>.</summary>
public sealed class CloudRangesSource
{
    /// <summary>Per-source enable. Off by default - the parent CloudRangesOptions.Enabled gates the whole set first.</summary>
    public bool Enabled { get; set; }

    /// <summary>Fetch URL. Override for internal mirrors.</summary>
    public string Url { get; set; } = "";

    /// <summary>Parser format. Known: <c>aws-json</c>, <c>azure-json</c>, <c>gcp-json</c>, <c>cidr-text</c>, <c>fastly-json</c>.</summary>
    public string Format { get; set; } = "";
}
