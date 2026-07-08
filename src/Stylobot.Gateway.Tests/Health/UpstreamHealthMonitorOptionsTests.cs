using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Health;
using Xunit;

namespace Stylobot.Gateway.Tests.Health;

/// <summary>
/// Tests for UpstreamHealthMonitorOptions binding and defaults.
/// </summary>
public class UpstreamHealthMonitorOptionsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var options = new UpstreamHealthMonitorOptions();
        options.ApplyDefaults();

        options.Enabled.Should().BeFalse();
        options.ProbeIntervalSeconds.Should().Be(60);
        options.ProbeTimeoutMs.Should().Be(2000);
        options.CandidatePaths.Should().HaveCount(9);
        options.CandidatePaths.Should().Contain("/health");
        options.CandidatePaths.Should().Contain("/healthz");
        options.CandidatePaths.Should().Contain("/livez");
        options.CandidatePaths.Should().Contain("/readyz");
        options.CandidatePaths.Should().Contain("/ready");
        options.CandidatePaths.Should().Contain("/live");
        options.CandidatePaths.Should().Contain("/ping");
        options.CandidatePaths.Should().Contain("/status");
        options.CandidatePaths.Should().Contain("/alive");
    }

    [Fact]
    public void SectionName_IsCorrect()
    {
        UpstreamHealthMonitorOptions.SectionName.Should().Be("BotDetection:UpstreamHealth");
    }

    [Fact]
    public void ConfigBinding_Enabled_ReflectsConfigValue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:UpstreamHealth:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<UpstreamHealthMonitorOptions>(
            config.GetSection(UpstreamHealthMonitorOptions.SectionName));

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<UpstreamHealthMonitorOptions>>().Value;

        opts.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ConfigBinding_CustomCandidatePaths_ReflectsConfigValue()
    {
        // When CandidatePaths is configured via array indices, the binder replaces
        // the array entirely. This test verifies that specifying CandidatePaths:0=/hc
        // results in a single-element array, not a union with the defaults.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:UpstreamHealth:Enabled"] = "true",
                ["BotDetection:UpstreamHealth:CandidatePaths:0"] = "/hc",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddUpstreamHealthMonitor(config);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<UpstreamHealthMonitorOptions>>().Value;

        opts.Enabled.Should().BeTrue();
        opts.CandidatePaths.Should().HaveCount(1);
        opts.CandidatePaths.Should().Contain("/hc");
    }

    [Fact]
    public void ConfigBinding_UnconfiguredPaths_UsesDefaults()
    {
        // When CandidatePaths is not configured, PostConfigure applies the 9-path default.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:UpstreamHealth:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddUpstreamHealthMonitor(config);

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<UpstreamHealthMonitorOptions>>().Value;

        opts.Enabled.Should().BeTrue();
        opts.CandidatePaths.Should().HaveCount(9);
        opts.CandidatePaths.Should().Contain("/health");
        opts.CandidatePaths.Should().Contain("/healthz");
    }

    [Fact]
    public void ConfigBinding_ProbeIntervalAndTimeout_ReflectConfigValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:UpstreamHealth:ProbeIntervalSeconds"] = "30",
                ["BotDetection:UpstreamHealth:ProbeTimeoutMs"] = "500",
            })
            .Build();

        var services = new ServiceCollection();
        services.Configure<UpstreamHealthMonitorOptions>(
            config.GetSection(UpstreamHealthMonitorOptions.SectionName));

        var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<UpstreamHealthMonitorOptions>>().Value;

        opts.ProbeIntervalSeconds.Should().Be(30);
        opts.ProbeTimeoutMs.Should().Be(500);
    }
}
