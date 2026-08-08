using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Declares the two GeoIP fetch sources to the fetch registry: MaxMind's own GeoLite2 binary DB
///     download, and the DataHub CSV fallback path. MaxMind is the one source in the whole registry
///     with real live-state tracking today (<see cref="GeoLite2UpdateService.LastSuccessfulFetchUtc"/>/
///     <c>LastFailedFetchUtc</c>) — added specifically because this was the operator's prime suspect
///     for "is it actually downloading" (see the dl- mission's MaxMind investigation).
/// </summary>
internal sealed class GeoDetectionFetchSourceContributor : IFetchSourceContributor
{
    private readonly IOptions<GeoLite2Options> _options;
    private readonly GeoLite2UpdateService? _updateService;

    // GeoLite2UpdateService is nullable: a host that wires AddGeoDetectionContributor without
    // AddGeoRouting (unusual, but not this class's job to forbid) won't have it registered, and
    // this contributor must not take the whole IFetchSourceRegistry down over a missing optional
    // dependency. See feedback_remote_mode_optional_di.
    public GeoDetectionFetchSourceContributor(IOptions<GeoLite2Options> options, GeoLite2UpdateService? updateService = null)
    {
        _options = options;
        _updateService = updateService;
    }

    public IEnumerable<FetchSourceStatus> GetSources()
    {
        var opts = _options.Value;

        // Mirrors GeoLite2UpdateService.DownloadDatabaseAsync's dbName switch exactly - don't let
        // this drift into a second, subtly-wrong copy of that mapping.
        var dbName = opts.DatabaseType switch
        {
            GeoLite2DatabaseType.City => "GeoLite2-City",
            GeoLite2DatabaseType.Country => "GeoLite2-Country",
            GeoLite2DatabaseType.ASN => "GeoLite2-ASN",
            _ => "GeoLite2-City"
        };

        yield return new FetchSourceStatus(
            "GeoLite2MaxMind", "MaxMind GeoLite2 Database",
            $"{opts.MaxMindDownloadBaseUrl}/{dbName}/download?suffix=tar.gz",
            Enabled: opts.IsAutoDownloadConfigured && opts.EnableAutoUpdate,
            Purpose: "City/Country/ASN-level IP geolocation binary database. Requires a MaxMind account " +
                     "(AccountId+LicenseKey, never logged/exposed here) — with no default and fail-open, " +
                     "this is the prime candidate for silently serving a stale bundled/manually-placed " +
                     ".mmdb with zero alarm if never configured.",
            Licence: "MaxMind GeoLite2 EULA — free tier, redistribution restricted, see maxmind.com/en/geolite2/eula",
            Cadence: $"one-shot on startup (if missing) + Tick1h, {opts.UpdateCheckInterval} since last success (gate: file age > 7 days)",
            FailureMode: FetchFailureMode.FailOpen,
            OnDiskLocation: opts.DatabasePath,
            LastSuccessUtc: _updateService?.LastSuccessfulFetchUtc,
            LastFailureUtc: _updateService?.LastFailedFetchUtc,
            HasLiveState: _updateService is not null);

        yield return new FetchSourceStatus(
            "GeoIpDataHubCsv", "DataHub GeoIP2-IPv4 CSV",
            opts.DataHubCsvUrl,
            Enabled: opts.Provider == GeoProvider.DataHubCsv,
            Purpose: "Free country-level IP database, no MaxMind account required. Alternate provider path " +
                     "to MaxMind's own binary DB — only active when Provider=DataHubCsv.",
            Licence: "DataHub core dataset (datahub.io/core/geoip2-ipv4), no redistribution restriction stated",
            Cadence: "manual/CLI setup-resource trigger only (ISetupResource) — no scheduled auto-refresh; CheckAsync flags Stale past 7 days but nothing re-triggers a download automatically",
            FailureMode: FetchFailureMode.FailClosed, // DownloadAsync has no try/catch - EnsureSuccessStatusCode throws to the caller
            OnDiskLocation: null, // computed per-instance from DatabasePath in GeoIpSetupResource; not exposed here to avoid duplicating that logic
            LastSuccessUtc: null, LastFailureUtc: null, HasLiveState: false);
    }
}
