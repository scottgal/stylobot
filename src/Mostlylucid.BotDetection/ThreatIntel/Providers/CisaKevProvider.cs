using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ThreatIntel.Providers;

/// <summary>
///     CISA Known Exploited Vulnerabilities catalog. Free, ~1MB JSON, lists CVEs
///     that are <em>actively exploited in the wild</em> - the right prior for a
///     CVE-probe detector to lean on. Hits on CVE matches set
///     <c>threatintel.kev_match = &lt;cveID&gt;</c> + lift the score to
///     <c>kev_match_threat_floor</c> (default 0.7), or 0.95 when the entry has
///     <c>knownRansomwareCampaignUse == "Known"</c>.
///
///     <para>Lookup key: subject type <see cref="ThreatSubjectType.Cve"/>, value
///     is the canonical uppercase CVE id (e.g. <c>"CVE-2021-44228"</c>).</para>
/// </summary>
internal sealed class CisaKevProvider : IThreatIntelProvider
{
    private readonly HttpClient _http;
    private readonly CisaKevOptions _options;
    private readonly ILogger<CisaKevProvider> _logger;
    private volatile FrozenDictionary<string, KevEntry>? _cache;
    private DateTime _lastRefreshUtc;

    public CisaKevProvider(
        HttpClient http,
        IOptions<BotDetectionOptions> options,
        ILogger<CisaKevProvider> logger)
    {
        _http = http;
        _options = options.Value.ThreatIntel.Providers.CisaKev;
        _logger = logger;
    }

    public string Name => "cisa-kev";
    public ThreatIntelMode Mode => ThreatIntelMode.Offline;
    public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Cve };
    public TimeSpan RefreshInterval => TimeSpan.FromHours(_options.RefreshHours);
    private bool _lastRefreshFailed;

    public ProviderStatus GetStatus() => new()
    {
        Provider = Name,
        Mode = Mode,
        Enabled = _options.Enabled,
        CacheSize = _cache?.Count ?? 0,
        LastRefreshUtc = _lastRefreshUtc == default ? null : _lastRefreshUtc,
        RefreshInterval = RefreshInterval,
        LastRefreshFailed = _lastRefreshFailed
    };

    public ThreatIntelVerdict? TryLookup(ThreatSubject subject)
    {
        if (subject.Type != ThreatSubjectType.Cve) return null;
        var cache = _cache;
        if (cache is null) return null;
        if (!cache.TryGetValue(subject.Value.ToUpperInvariant(), out var entry)) return null;

        // Ransomware-confirmed entries get a higher confidence floor so the
        // contributor's kev_match handling can lean on them more aggressively.
        var ransomware = string.Equals(entry.KnownRansomwareCampaignUse, "Known", StringComparison.OrdinalIgnoreCase);
        return new ThreatIntelVerdict
        {
            Provider = Name,
            Classification = "kev",
            Confidence = ransomware ? 0.95 : 0.7,
            IntelligenceClass = IntelligenceSignalClass.Vulnerability,
            ObservedUtc = _lastRefreshUtc,
            ExpiresUtc = default,
            Metadata = new Dictionary<string, string>
            {
                ["vendor"] = entry.VendorProject ?? "",
                ["product"] = entry.Product ?? "",
                ["date_added"] = entry.DateAdded ?? "",
                ["ransomware"] = ransomware ? "true" : "false"
            }
        };
    }

    public async Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_options.Url)) return;
        try
        {
            await using var stream = await _http.GetStreamAsync(_options.Url, cancellationToken);
            var catalog = await JsonSerializer.DeserializeAsync(
                stream, CisaKevJsonContext.Default.KevCatalog, cancellationToken);
            if (catalog?.Vulnerabilities is null) return;

            var dict = new Dictionary<string, KevEntry>(catalog.Vulnerabilities.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var v in catalog.Vulnerabilities)
            {
                if (string.IsNullOrEmpty(v.CveID)) continue;
                dict[v.CveID.ToUpperInvariant()] = v;
            }
            _cache = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
            _lastRefreshUtc = DateTime.UtcNow;
            _lastRefreshFailed = false;
            _logger.LogInformation("{Provider}: loaded {Count} KEV entries (catalog {Version})",
                Name, _cache.Count, catalog.CatalogVersion ?? "?");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _lastRefreshFailed = true;
            _logger.LogWarning(ex,
                "{Provider}: refresh failed; keeping previous cache ({Count} entries, last refreshed {LastRefresh:O})",
                Name, _cache?.Count ?? 0, _lastRefreshUtc);
        }
    }

    // === KEV catalog DTO ===
    // Each field maps to the canonical CISA JSON property name. Source-gen via
    // CisaKevJsonContext below keeps this AOT-clean.
    internal sealed class KevCatalog
    {
        [JsonPropertyName("catalogVersion")] public string? CatalogVersion { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("vulnerabilities")] public List<KevEntry>? Vulnerabilities { get; set; }
    }

    internal sealed class KevEntry
    {
        [JsonPropertyName("cveID")] public string? CveID { get; set; }
        [JsonPropertyName("vendorProject")] public string? VendorProject { get; set; }
        [JsonPropertyName("product")] public string? Product { get; set; }
        [JsonPropertyName("vulnerabilityName")] public string? VulnerabilityName { get; set; }
        [JsonPropertyName("dateAdded")] public string? DateAdded { get; set; }
        [JsonPropertyName("knownRansomwareCampaignUse")] public string? KnownRansomwareCampaignUse { get; set; }
    }
}

[JsonSerializable(typeof(CisaKevProvider.KevCatalog))]
[JsonSerializable(typeof(CisaKevProvider.KevEntry))]
[JsonSerializable(typeof(List<CisaKevProvider.KevEntry>))]
internal partial class CisaKevJsonContext : JsonSerializerContext;

/// <summary>Per-vendor config for the CISA KEV provider.</summary>
public sealed class CisaKevOptions
{
    /// <summary>Master enable flag. FOSS default: off.</summary>
    public bool Enabled { get; set; }

    /// <summary>Catalog URL. Override for internal mirrors.</summary>
    public string Url { get; set; } = "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json";

    /// <summary>Refresh cadence in hours. CISA updates this catalog on a roughly daily cadence.</summary>
    public double RefreshHours { get; set; } = 1;
}
