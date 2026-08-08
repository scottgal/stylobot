using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Contributor;

/// <summary>
///     Declares the two GeoIP fetch sources to the fetch registry: MaxMind's own GeoLite2 binary DB
///     download, and the DataHub CSV fallback path. MaxMind is the one source in the whole registry
///     declared with <c>HasLiveState: true</c> — added specifically because this was the operator's
///     prime suspect for "is it actually downloading" (the dl- mission's MaxMind investigation). Its
///     observed state is persisted via <see cref="GeoLite2StatePersistenceBridge"/>, not carried on
///     this declaration — see <see cref="FetchSourceDeclaration"/> for why the split exists.
/// </summary>
internal sealed class GeoDetectionFetchSourceContributor : IFetchSourceContributor
{
    /// <summary>Shared with <see cref="GeoLite2StatePersistenceBridge"/> so the id this declaration uses and the id observations are recorded under can never drift apart.</summary>
    public const string MaxMindSourceId = "GeoLite2MaxMind";

    private readonly IOptions<GeoLite2Options> _options;

    public GeoDetectionFetchSourceContributor(IOptions<GeoLite2Options> options)
    {
        _options = options;
    }

    public IEnumerable<FetchSourceDeclaration> GetSources()
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

        yield return new FetchSourceDeclaration(
            MaxMindSourceId, "MaxMind GeoLite2 Database",
            $"{opts.MaxMindDownloadBaseUrl}/{dbName}/download?suffix=tar.gz",
            Enabled: opts.IsAutoDownloadConfigured && opts.EnableAutoUpdate,
            Purpose: "City/Country/ASN-level IP geolocation binary database. Requires a MaxMind account " +
                     "(AccountId+LicenseKey, never logged/exposed here) — with no default and fail-open, " +
                     "this is the prime candidate for silently serving a stale bundled/manually-placed " +
                     ".mmdb with zero alarm if never configured.",
            Licence: "MaxMind GeoLite2 EULA — free tier, redistribution restricted, see maxmind.com/en/geolite2/eula",
            Cadence: $"one-shot on startup (if missing) + Tick1h, {opts.UpdateCheckInterval} since last success (gate: file age > 7 days)",
            // The real staleness gate GeoLite2UpdateService.CheckForUpdateAsync enforces is a
            // hardcoded 7-day file-age check, NOT UpdateCheckInterval (which only controls how
            // often that check itself runs) - CadenceInterval must match the gate that actually
            // determines "due for refresh", or GetHealthState computes staleness against the
            // wrong number.
            CadenceInterval: TimeSpan.FromDays(7),
            FailureMode: FetchFailureMode.FailOpen,
            OnDiskLocation: opts.DatabasePath,
            HasLiveState: true);

        yield return new FetchSourceDeclaration(
            "GeoIpDataHubCsv", "DataHub GeoIP2-IPv4 CSV",
            opts.DataHubCsvUrl,
            Enabled: opts.Provider == GeoProvider.DataHubCsv,
            Purpose: "Free country-level IP database, no MaxMind account required. Alternate provider path " +
                     "to MaxMind's own binary DB — only active when Provider=DataHubCsv.",
            Licence: "DataHub core dataset (datahub.io/core/geoip2-ipv4), no redistribution restriction stated",
            Cadence: "manual/CLI setup-resource trigger only (ISetupResource) — no scheduled auto-refresh; CheckAsync flags Stale past 7 days but nothing re-triggers a download automatically",
            CadenceInterval: null, // no scheduled cadence to measure staleness against - HasLiveState is false anyway
            FailureMode: FetchFailureMode.FailClosed, // DownloadAsync has no try/catch - EnsureSuccessStatusCode throws to the caller
            OnDiskLocation: null, // computed per-instance from DatabasePath in GeoIpSetupResource; not exposed here to avoid duplicating that logic
            HasLiveState: false);
    }
}
