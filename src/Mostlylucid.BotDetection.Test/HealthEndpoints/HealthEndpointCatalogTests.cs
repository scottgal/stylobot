using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.HealthEndpoints;
using Xunit;

namespace Mostlylucid.BotDetection.Test.HealthEndpoints;

/// <summary>
///     Verifies <see cref="HealthEndpointCatalog.IsHealthPath"/> returns the expected
///     result for both default health paths and non-health paths.
/// </summary>
public sealed class HealthEndpointCatalogTests
{
    [Theory]
    [InlineData("/health", true)]
    [InlineData("/healthz", true)]
    [InlineData("/livez", true)]
    [InlineData("/readyz", true)]
    [InlineData("/ready", true)]
    [InlineData("/live", true)]
    [InlineData("/ping", true)]
    [InlineData("/status", true)]
    [InlineData("/alive", true)]
    [InlineData("/admin/alive", true)]
    [InlineData("/api/products", false)]
    [InlineData("/", false)]
    [InlineData("/healthcheck", false)]   // must NOT match /health via substring
    [InlineData("/HEALTH", true)]         // case-insensitive
    [InlineData("/Health", true)]         // mixed case
    [InlineData("/health/liveness", true)]  // sub-path match via StartsWithSegments
    [InlineData("/ping/detailed", true)]    // sub-path match via StartsWithSegments
    public void IsHealthPath_MatchesDefaults(string path, bool expected)
        => Assert.Equal(expected, new HealthEndpointCatalog(Options.Create(HealthEndpointOptions.Default)).IsHealthPath(path));

    [Fact]
    public void Custom_paths_are_recognized()
    {
        var opts = new HealthEndpointOptions { Paths = ["/custom-health", "/probe"] };
        var catalog = new HealthEndpointCatalog(Options.Create(opts));

        Assert.True(catalog.IsHealthPath("/custom-health"));
        Assert.True(catalog.IsHealthPath("/probe"));
        Assert.False(catalog.IsHealthPath("/health")); // not in this custom set
    }

    [Fact]
    public void Default_property_has_all_ten_standard_paths()
    {
        var defaults = HealthEndpointOptions.Default;
        Assert.Equal(10, defaults.Paths.Count);
    }

    [Fact]
    public void Config_binding_is_live_custom_path_replaces_defaults()
    {
        // Prove the BotDetection:HealthEndpoints:Paths config surface is live.
        // Paths starts empty in HealthEndpointOptions so binding replaces, not appends.
        // PostConfigure supplies defaults only when the operator provides nothing.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:HealthEndpoints:Paths:0"] = "/custom-health",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<HealthEndpointOptions>(
            config.GetSection(HealthEndpointOptions.SectionName));
        services.AddSingleton<HealthEndpointCatalog>();
        var sp = services.BuildServiceProvider();

        var catalog = sp.GetRequiredService<HealthEndpointCatalog>();

        // Custom path is recognized; default paths are NOT (config replaces defaults).
        Assert.True(catalog.IsHealthPath("/custom-health"),
            "/custom-health must be recognized when configured");
        Assert.False(catalog.IsHealthPath("/health"),
            "/health must NOT be recognized when config provides a replacement list");
    }
}
