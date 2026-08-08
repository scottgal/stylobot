using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Contributor.Extensions;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     GeoDetectionFetchSourceContributor is the one contributor declared with
///     <c>HasLiveState: true</c> — the direct product of the dl- mission's MaxMind investigation.
///     Its observed state is persisted (not in-memory — see <see cref="IFetchSourceStateStore"/>),
///     so exercising it means going through <see cref="IFetchSourceRegistry.GetAllAsync"/>, not
///     constructing the internal contributor directly.
/// </summary>
public sealed class GeoDetectionFetchSourceContributorTests
{
    private static ServiceProvider BuildProvider(string statePath, bool wireGeoRouting = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBotDetection(new Action<Mostlylucid.BotDetection.Models.BotDetectionOptions>(o => o.DatabasePath = ""));
        services.Configure<FetchSourceStateStoreOptions>(o => o.FilePath = statePath);

        if (wireGeoRouting)
        {
            services.AddGeoRouting(configureProvider: o =>
            {
                o.AccountId = 12345;
                o.LicenseKey = "test-license-key";
                o.EnableAutoUpdate = true;
            });
        }

        services.AddGeoDetectionContributor();
        return services.BuildServiceProvider();
    }

    private static string TempStatePath() => Path.Combine(Path.GetTempPath(), $"geo-fetch-state-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Registry_includes_MaxMind_and_DataHub_entries_when_geo_is_fully_wired()
    {
        var statePath = TempStatePath();
        try
        {
            var registry = BuildProvider(statePath).GetRequiredService<IFetchSourceRegistry>();
            var sources = await registry.GetAllAsync();

            var maxmind = Assert.Single(sources, s => s.Id == "GeoLite2MaxMind");
            Assert.True(maxmind.Enabled);
            Assert.Contains("GeoLite2-City/download", maxmind.Url);
            Assert.True(maxmind.HasLiveState);
            Assert.Null(maxmind.LastSuccessUtc); // never fetched in this test - must read as "never", not "unknown"

            var dataHub = Assert.Single(sources, s => s.Id == "GeoIpDataHubCsv");
            Assert.False(dataHub.Enabled); // Provider defaults to MaxMindLocal, not DataHubCsv
            Assert.False(dataHub.HasLiveState);
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public async Task MaxMind_CadenceInterval_matches_the_real_7_day_staleness_gate_GeoLite2UpdateService_enforces()
    {
        // overview-'s exact scenario: succeeded once, then silently stopped ticking. Must read as
        // Stale, not Healthy, once past the real gate GeoLite2UpdateService.CheckForUpdateAsync
        // uses (file age > 7 days) - not some other number invented for the registry.
        var statePath = TempStatePath();
        try
        {
            var registry = BuildProvider(statePath).GetRequiredService<IFetchSourceRegistry>();
            var maxmind = Assert.Single(await registry.GetAllAsync(), s => s.Id == "GeoLite2MaxMind");

            Assert.Equal(TimeSpan.FromDays(7), maxmind.CadenceInterval);

            var now = DateTimeOffset.UtcNow;
            var succeededLongAgo = maxmind with { LastSuccessUtc = now - TimeSpan.FromDays(90) };
            Assert.Equal(FetchHealthState.Stale, succeededLongAgo.GetHealthState(now));

            var succeededRecently = maxmind with { LastSuccessUtc = now - TimeSpan.FromDays(2) };
            Assert.Equal(FetchHealthState.Healthy, succeededRecently.GetHealthState(now));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }

    [Fact]
    public async Task Registry_does_not_throw_when_GeoDetectionContributor_is_wired_without_GeoRouting()
    {
        // Optional-DI fallback: AddGeoDetectionContributor without AddGeoRouting must not crash the
        // whole fetch registry over a missing GeoLite2UpdateService. HasLiveState stays true (it's a
        // static declaration of intent), but since GeoLite2StatePersistenceBridge no-ops without an
        // update service to subscribe to, nothing is ever recorded - reads as never-attempted.
        var statePath = TempStatePath();
        try
        {
            var registry = BuildProvider(statePath, wireGeoRouting: false).GetRequiredService<IFetchSourceRegistry>();
            var maxmind = Assert.Single(await registry.GetAllAsync(), s => s.Id == "GeoLite2MaxMind");

            Assert.True(maxmind.HasLiveState);
            Assert.Null(maxmind.LastSuccessUtc);
            Assert.Equal(FetchHealthState.NeverAttempted, maxmind.GetHealthState(DateTimeOffset.UtcNow));
        }
        finally
        {
            if (File.Exists(statePath)) File.Delete(statePath);
        }
    }
}
