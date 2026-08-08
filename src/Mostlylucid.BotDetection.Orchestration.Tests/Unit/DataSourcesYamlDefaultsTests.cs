using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
}
