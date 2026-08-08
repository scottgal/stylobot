using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Definitions.WellKnownBots;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.BotDetection.WebBotAuth;

namespace Mostlylucid.BotDetection.Data.Sources;

/// <summary>
///     Declares every fetch source this package owns to the fetch registry: the 12
///     <see cref="DataSourcesOptions"/> sources, <see cref="WellKnownBotsOptions"/>, the 4 FOSS
///     ThreatIntel providers, and (declared but not YAML-seeded — see class docs on why) TlsCorpus
///     and PublicKeyRegistry. Purpose/licence text for the 17 YAML-backed sources comes from the
///     same manifests <see cref="DataSourcesYamlDefaultsConfigurator"/> seeds defaults from — never
///     re-typed here, or this class becomes exactly the second source of truth the registry exists
///     to prevent. Live Enabled/Url values come from the current <see cref="IOptions{TOptions}"/>
///     snapshot, so config overrides show up correctly, not just the YAML default.
/// </summary>
internal sealed class BotDetectionFetchSourceContributor : IFetchSourceContributor
{
    private readonly IOptions<BotDetectionOptions> _options;
    private readonly IOptions<PublicKeyRegistryOptions> _publicKeyRegistryOptions;
    private readonly DataSourceManifestLoader _manifestLoader;

    public BotDetectionFetchSourceContributor(
        IOptions<BotDetectionOptions> options,
        IOptions<PublicKeyRegistryOptions> publicKeyRegistryOptions,
        DataSourceManifestLoader manifestLoader)
    {
        _options = options;
        _publicKeyRegistryOptions = publicKeyRegistryOptions;
        _manifestLoader = manifestLoader;
    }

    public IEnumerable<FetchSourceDeclaration> GetSources()
    {
        var opts = _options.Value;
        var manifest = _manifestLoader.LoadEmbeddedManifests();
        var ds = opts.DataSources;

        // BotListUpdateService gates the actual fetch on 24h-since-last-success (60m check
        // interval) for every DataSources entry - same structured cadence for all twelve.
        var dataSourceCadence = TimeSpan.FromHours(24);
        const string dataSourceCadenceLabel = "BotListUpdateService, Tick1h subscription, 24h since last success, floor 60m checks";
        const string dataSourceOnDisk = "Bot list: botdetection.db (SQLite, via BotListDatabase)";

        yield return FromDataSource(manifest, "IsBot", ds.IsBot, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "Matomo", ds.Matomo, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "CrawlerUserAgents", ds.CrawlerUserAgents, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "AwsIpRanges", ds.AwsIpRanges, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "GcpIpRanges", ds.GcpIpRanges, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "AzureIpRanges", ds.AzureIpRanges, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "CloudflareIpv4", ds.CloudflareIpv4, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "CloudflareIpv6", ds.CloudflareIpv6, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "VpnAsns", ds.VpnAsns, "Bot list: botdetection.db (falls back to YAML seeds in ip.detector.yaml)", dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "BrowserVersions", ds.BrowserVersions, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "ScannerUserAgents", ds.ScannerUserAgents, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);
        yield return FromDataSource(manifest, "CoreRuleSetScanners", ds.CoreRuleSetScanners, dataSourceOnDisk, dataSourceCadenceLabel, dataSourceCadence);

        if (manifest.TryGetValue("WellKnownBots", out var wkb))
        {
            yield return new FetchSourceDeclaration(
                "WellKnownBots", "Arcjet Well-Known Bots", NullIfEmpty(opts.WellKnownBots.Url),
                Enabled: !string.IsNullOrWhiteSpace(opts.WellKnownBots.Url),
                Purpose: wkb.Purpose, Licence: wkb.Licence,
                Cadence: $"Tick1h, {opts.WellKnownBots.RefreshInterval} since last success (floor 1h)",
                CadenceInterval: opts.WellKnownBots.RefreshInterval,
                FailureMode: FetchFailureMode.FailOpen,
                OnDiskLocation: "in-memory only (WellKnownBotIndex); embedded baseline seeds cold start",
                HasLiveState: false);
        }

        var ti = opts.ThreatIntel.Providers;
        if (manifest.TryGetValue("CisaKev", out var cisaKev))
            yield return ThreatIntelSource("CisaKev", "CISA Known Exploited Vulnerabilities", cisaKev, ti.CisaKev.Url, ti.CisaKev.Enabled,
                $"{ti.CisaKev.RefreshHours}h, via ThreatIntelRefreshService", TimeSpan.FromHours(ti.CisaKev.RefreshHours));
        if (manifest.TryGetValue("TorExit", out var torExit))
            yield return ThreatIntelSource("TorExit", "Tor Exit Node List", torExit, ti.TorExit.Url, ti.TorExit.Enabled,
                $"{ti.TorExit.RefreshMinutes}min, via ThreatIntelRefreshService", TimeSpan.FromMinutes(ti.TorExit.RefreshMinutes));
        if (manifest.TryGetValue("SpamhausDrop", out var spamhaus))
            yield return ThreatIntelSource("SpamhausDrop", "Spamhaus DROP/EDROP", spamhaus, ti.SpamhausDrop.Url, ti.SpamhausDrop.Enabled,
                $"{ti.SpamhausDrop.RefreshHours}h, via ThreatIntelRefreshService", TimeSpan.FromHours(ti.SpamhausDrop.RefreshHours));
        if (manifest.TryGetValue("CloudRangesFastly", out var fastly))
            yield return ThreatIntelSource("CloudRangesFastly", "Fastly Public IP List", fastly, ti.CloudRanges.Fastly.Url, ti.CloudRanges.Fastly.Enabled,
                $"{ti.CloudRanges.RefreshHours}h, via ThreatIntelRefreshService", TimeSpan.FromHours(ti.CloudRanges.RefreshHours));

        // No YAML entry: neither has a shipped default URL to seed (both idle-until-configured
        // by design), so there is nothing for a manifest to declare beyond what's below.
        yield return new FetchSourceDeclaration(
            "TlsCorpus", "TLS/JA3 Reference Corpus", NullIfEmpty(opts.TlsCorpus.RefreshUrl),
            Enabled: opts.TlsCorpus.Enabled,
            Purpose: "Signed JA3 fingerprint reference corpus for TLS-based bot detection. No default URL — opt-in, operator must supply a signed envelope source. Embedded baseline covers cold start regardless.",
            Licence: "Operator-supplied (signed envelope from the vendor pipeline); not a third-party feed",
            Cadence: $"Tick1h, {opts.TlsCorpus.RefreshInterval} since last success (floor 5min)",
            CadenceInterval: opts.TlsCorpus.RefreshInterval,
            FailureMode: FetchFailureMode.FailOpen,
            OnDiskLocation: "in-memory only (IJa3ReferenceIndex); embedded baseline seeds cold start",
            HasLiveState: false);

        var pkr = _publicKeyRegistryOptions.Value;
        yield return new FetchSourceDeclaration(
            "PublicKeyRegistry", "Web-Bot-Auth Public Key Registry", NullIfEmpty(pkr.ManifestUrl),
            Enabled: pkr.Enabled,
            Purpose: "Web-Bot-Auth (RFC 9421 signature) public-key manifest, e.g. Cloudflare's AI-agent registry. No default URL — opt-in; manual keys always work regardless of remote fetch.",
            Licence: "Depends on configured manifest source; none shipped by default",
            Cadence: $"Tick1h, {pkr.RefreshInterval} since last success (floor 1h)",
            CadenceInterval: pkr.RefreshInterval,
            FailureMode: FetchFailureMode.FailOpen,
            OnDiskLocation: pkr.SnapshotFilePath ?? "in-memory only (no snapshot path configured)",
            HasLiveState: false);
    }

    private static FetchSourceDeclaration FromDataSource(
        IReadOnlyDictionary<string, DataSourceManifestEntry> manifest, string id, DataSourceConfig live,
        string onDisk, string cadence, TimeSpan cadenceInterval)
    {
        manifest.TryGetValue(id, out var entry);
        return new FetchSourceDeclaration(
            id, id, NullIfEmpty(live.Url), live.Enabled,
            Purpose: entry?.Purpose ?? live.Description,
            Licence: entry?.Licence ?? live.Licence,
            Cadence: cadence, CadenceInterval: cadenceInterval, FailureMode: FetchFailureMode.FailOpen, OnDiskLocation: onDisk,
            HasLiveState: false);
    }

    private static FetchSourceDeclaration ThreatIntelSource(
        string id, string displayName, DataSourceManifestEntry entry, string liveUrl, bool liveEnabled,
        string cadence, TimeSpan cadenceInterval)
        => new(id, displayName, NullIfEmpty(liveUrl), liveEnabled, entry.Purpose, entry.Licence, cadence, cadenceInterval,
            FetchFailureMode.FailOpen, // steady-state is fail-open; BlockStartupOnFirstFetch adds a separate fail-closed bootstrap gate on top
            OnDiskLocation: "in-memory cache only (ThreatIntelCoordinator); no disk persistence",
            HasLiveState: false);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
