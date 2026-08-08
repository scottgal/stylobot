using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Sources;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Verifies the fetch-source registry precedence: YAML manifests
///     (<c>Data/Sources/*.source.yaml</c>) ship the defaults, and
///     <c>appsettings</c>/env configuration overrides them per-field — including
///     disabling a source that ships enabled, and enabling one that ships disabled.
///     Guards against regressing to hardcoded C# literal defaults.
/// </summary>
public class DataSourcesYamlDefaultsTests
{
    private static Dictionary<string, string?> BaseConfig() => new()
    {
        // Unrelated required setting (SQLite path) - just needs a value so
        // options validation doesn't block resolving IOptions<BotDetectionOptions>.
        ["BotDetection:DatabasePath"] = ""
    };

    [Fact]
    public void YamlManifest_seeds_defaults_when_no_config_present()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        Assert.True(options.DataSources.IsBot.Enabled);
        Assert.Equal(
            "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json",
            options.DataSources.IsBot.Url);
        Assert.False(options.DataSources.Matomo.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.DataSources.IsBot.Description));
        Assert.Equal("MIT (github.com/omrilotan/isbot)", options.DataSources.IsBot.Licence);
    }

    [Fact]
    public void Config_disables_a_source_that_ships_enabled_by_default()
    {
        var configValues = BaseConfig();
        configValues["BotDetection:DataSources:IsBot:Enabled"] = "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        Assert.False(options.DataSources.IsBot.Enabled);
        // The URL wasn't touched by config, so the YAML default must survive.
        Assert.Equal(
            "https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json",
            options.DataSources.IsBot.Url);
    }

    [Fact]
    public void Config_enables_a_source_that_ships_disabled_and_overrides_its_url()
    {
        var configValues = BaseConfig();
        configValues["BotDetection:DataSources:Matomo:Enabled"] = "true";
        configValues["BotDetection:DataSources:Matomo:Url"] = "https://internal-mirror.example.com/bots.yml";
        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        Assert.True(options.DataSources.Matomo.Enabled);
        Assert.Equal("https://internal-mirror.example.com/bots.yml", options.DataSources.Matomo.Url);
    }

    [Fact]
    public void WellKnownBots_seeds_from_yaml_and_config_can_disable_via_empty_url()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        Assert.Equal(
            "https://raw.githubusercontent.com/arcjet/well-known-bots/main/well-known-bots.json",
            options.WellKnownBots.Url);

        // WellKnownBotsOptions has no separate Enabled flag - "" is how this source is
        // disabled (see WellKnownBotRefreshService.OnTickAsync), so config setting an
        // empty Url must still be honored as a real override, not treated as "unset".
        var disableValues = BaseConfig();
        disableValues["BotDetection:WellKnownBots:Url"] = "";
        var disableConfig = new ConfigurationBuilder().AddInMemoryCollection(disableValues).Build();
        var disableServices = new ServiceCollection();
        disableServices.AddSingleton<IConfiguration>(disableConfig);
        disableServices.AddBotDetection(disableConfig);
        var disabledOptions = disableServices.BuildServiceProvider()
            .GetRequiredService<IOptions<BotDetectionOptions>>().Value;

        Assert.Equal("", disabledOptions.WellKnownBots.Url);
    }

    [Fact]
    public void ThreatIntel_providers_seed_from_yaml_including_secondary_url()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        var providers = options.ThreatIntel.Providers;

        // All four are FOSS-disabled-by-default (opt-in posture); the URLs must still
        // seed from YAML so an operator flipping Enabled=true gets a working default.
        Assert.False(providers.CisaKev.Enabled);
        Assert.Equal(
            "https://www.cisa.gov/sites/default/files/feeds/known_exploited_vulnerabilities.json",
            providers.CisaKev.Url);

        Assert.False(providers.TorExit.Enabled);
        Assert.Equal("https://check.torproject.org/torbulkexitlist", providers.TorExit.Url);

        Assert.False(providers.SpamhausDrop.Enabled);
        Assert.Equal("https://www.spamhaus.org/drop/drop.txt", providers.SpamhausDrop.Url);
        Assert.Equal("https://www.spamhaus.org/drop/edrop.txt", providers.SpamhausDrop.EdropUrl);

        Assert.False(providers.CloudRanges.Fastly.Enabled);
        Assert.Equal("https://api.fastly.com/public-ip-list", providers.CloudRanges.Fastly.Url);
    }

    [Fact]
    public void FetchSourceRegistry_declares_every_registered_source_with_no_duplicate_ids()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddBotDetection(config);

        var registry = services.BuildServiceProvider().GetRequiredService<IFetchSourceRegistry>();
        var sources = registry.GetDeclarations();

        // 12 DataSources + WellKnownBots + 4 ThreatIntel providers + TlsCorpus + PublicKeyRegistry
        // + 2 list_updates buckets (BotPatternsGroup/DatacenterIpsGroup).
        Assert.Equal(21, sources.Count);
        Assert.Equal(sources.Select(s => s.Id).Distinct().Count(), sources.Count);

        // Every source must carry a non-empty Purpose - an empty one is exactly the kind of
        // "declared but not actually informative" entry the registry exists to prevent.
        Assert.All(sources, s => Assert.False(string.IsNullOrWhiteSpace(s.Purpose), $"{s.Id} has no Purpose"));

        var isBot = Assert.Single(sources, s => s.Id == "IsBot");
        Assert.True(isBot.Enabled);
        Assert.Equal("https://raw.githubusercontent.com/omrilotan/isbot/main/src/patterns.json", isBot.Url);
        // Individual DataSources entries never claim per-source precision the DB can't back up -
        // ruling (overview-, 2026-08-08): that's the bucket entries' job, not theirs.
        Assert.False(isBot.HasLiveState);

        // TlsCorpus/PublicKeyRegistry have no shipped default - disabled, no URL, but still declared.
        var tlsCorpus = Assert.Single(sources, s => s.Id == "TlsCorpus");
        Assert.False(tlsCorpus.Enabled);
        Assert.Null(tlsCorpus.Url);

        var botPatternsGroup = Assert.Single(sources, s => s.Id == "BotPatternsGroup");
        Assert.True(botPatternsGroup.HasLiveState);
        Assert.Equal(["IsBot", "Matomo", "CrawlerUserAgents"], botPatternsGroup.GroupedSourceIds);

        var datacenterIpsGroup = Assert.Single(sources, s => s.Id == "DatacenterIpsGroup");
        Assert.True(datacenterIpsGroup.HasLiveState);
        Assert.Equal(
            ["AwsIpRanges", "GcpIpRanges", "AzureIpRanges", "CloudflareIpv4", "CloudflareIpv6"],
            datacenterIpsGroup.GroupedSourceIds);
    }

    [Fact]
    public async Task Observed_state_survives_a_fresh_DI_container_reading_the_same_persisted_file()
    {
        // The exact defect overview- flagged: an in-memory LastSuccessUtc resets to null on every
        // restart. A fresh ServiceProvider standing in for "the process restarted" must still see a
        // success recorded by the previous one, because it's read from a file, not a field.
        var tempPath = Path.Combine(Path.GetTempPath(), $"fetch-source-state-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(BaseConfig()).Build();
            var recordedAt = DateTimeOffset.UtcNow;

            var firstProcessServices = new ServiceCollection();
            firstProcessServices.AddSingleton<IConfiguration>(config);
            firstProcessServices.AddBotDetection(config);
            firstProcessServices.Configure<FetchSourceStateStoreOptions>(o => o.FilePath = tempPath);
            var firstProcessProvider = firstProcessServices.BuildServiceProvider();
            var stateStore = firstProcessProvider.GetRequiredService<IFetchSourceStateStore>();
            await stateStore.RecordSuccessAsync("IsBot", recordedAt);

            // A brand new container, sharing nothing with the first except the file path -
            // simulates a pod restart reading the same on-disk state.
            var secondProcessServices = new ServiceCollection();
            secondProcessServices.AddSingleton<IConfiguration>(config);
            secondProcessServices.AddBotDetection(config);
            secondProcessServices.Configure<FetchSourceStateStoreOptions>(o => o.FilePath = tempPath);
            var secondProcessProvider = secondProcessServices.BuildServiceProvider();
            var registry = secondProcessProvider.GetRequiredService<IFetchSourceRegistry>();

            var statuses = await registry.GetAllAsync();
            var isBot = Assert.Single(statuses, s => s.Id == "IsBot");

            Assert.Equal(recordedAt, isBot.LastSuccessUtc);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
