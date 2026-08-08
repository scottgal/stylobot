using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Contributor.Extensions;
using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     GeoDetectionFetchSourceContributor is the one contributor whose MaxMind entry carries real
///     live-state (HasLiveState=true) — the direct product of the dl- mission's MaxMind
///     investigation (LastSuccessfulFetchUtc/LastFailedFetchUtc). Exercises it the same way
///     production code does: through DI registration and the public IFetchSourceRegistry, not by
///     constructing the internal contributor directly.
/// </summary>
public sealed class GeoDetectionFetchSourceContributorTests
{
    [Fact]
    public void Registry_includes_MaxMind_and_DataHub_entries_when_geo_is_fully_wired()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBotDetection(new Action<Mostlylucid.BotDetection.Models.BotDetectionOptions>(o => o.DatabasePath = ""));
        services.AddGeoRouting(
            configureProvider: o =>
            {
                o.AccountId = 12345;
                o.LicenseKey = "test-license-key";
                o.EnableAutoUpdate = true;
            });
        services.AddGeoDetectionContributor();

        var registry = services.BuildServiceProvider().GetRequiredService<IFetchSourceRegistry>();
        var sources = registry.GetAll();

        var maxmind = Assert.Single(sources, s => s.Id == "GeoLite2MaxMind");
        Assert.True(maxmind.Enabled);
        Assert.Contains("GeoLite2-City/download", maxmind.Url);
        Assert.True(maxmind.HasLiveState);
        Assert.Null(maxmind.LastSuccessUtc); // never fetched in this test - must read as "never", not "unknown"

        var dataHub = Assert.Single(sources, s => s.Id == "GeoIpDataHubCsv");
        Assert.False(dataHub.Enabled); // Provider defaults to MaxMindLocal, not DataHubCsv
        Assert.False(dataHub.HasLiveState);
    }

    [Fact]
    public void Registry_does_not_throw_when_GeoDetectionContributor_is_wired_without_GeoRouting()
    {
        // Optional-DI fallback: AddGeoDetectionContributor without AddGeoRouting must not crash
        // the whole fetch registry over a missing GeoLite2UpdateService.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddBotDetection(new Action<Mostlylucid.BotDetection.Models.BotDetectionOptions>(o => o.DatabasePath = ""));
        services.AddGeoDetectionContributor();

        var registry = services.BuildServiceProvider().GetRequiredService<IFetchSourceRegistry>();
        var maxmind = Assert.Single(registry.GetAll(), s => s.Id == "GeoLite2MaxMind");

        Assert.False(maxmind.HasLiveState);
        Assert.Null(maxmind.LastSuccessUtc);
    }
}
