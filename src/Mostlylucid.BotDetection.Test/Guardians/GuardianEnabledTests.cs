using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Guardians;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Tests for per-guardian <see cref="IGuardian.Enabled"/> and
///     <see cref="GuardianConfig.Read"/>: disabled guardians stay on the roster
///     but are skipped by the walker; enabled guardians run normally.
/// </summary>
public sealed class GuardianEnabledTests
{
    private sealed class FakeGuardian : IGuardian
    {
        public string Name { get; init; } = "fake";
        public GuardianCategory Category { get; init; } = GuardianCategory.Data;
        public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(20);
        public bool Enabled { get; init; } = true;
        public int Runs;

        public Task<GuardianReport> GuardAsync(CancellationToken ct = default)
        {
            Runs++;
            return Task.FromResult(GuardianReport.Ok(this, 1.0));
        }
    }

    private static GuardianService Svc(params IGuardian[] gs) =>
        new(gs, NullLogger<GuardianService>.Instance);

    // --- Enabled/disabled walker behaviour ---

    [Fact]
    public async Task Disabled_guardian_is_skipped_but_stays_on_roster()
    {
        var disabled = new FakeGuardian { Name = "off", Enabled = false };
        var enabled = new FakeGuardian { Name = "on", Enabled = true };
        var svc = Svc(disabled, enabled);

        await svc.RunDueAsync(DateTimeOffset.UtcNow);

        disabled.Runs.Should().Be(0, "disabled guardian must not be invoked");
        enabled.Runs.Should().Be(1, "enabled guardian must run");
        svc.Guardians.Should().HaveCount(2, "both guardians remain on the roster");
        svc.Guardians.Should().Contain(disabled, "disabled guardian is still on the roster");
    }

    [Fact]
    public async Task Enabled_guardian_runs_normally()
    {
        var g = new FakeGuardian { Name = "on", Enabled = true };
        var svc = Svc(g);

        await svc.RunDueAsync(DateTimeOffset.UtcNow);

        g.Runs.Should().Be(1);
        svc.LatestReports.Should().ContainKey("on");
    }

    [Fact]
    public void Default_Enabled_is_true_via_interface_default_member()
    {
        // A guardian that does NOT override Enabled must default to true.
        // FakeGuardian in GuardianServiceTests does not set Enabled, so this
        // verifies the default member exists and returns true.
        IGuardian g = new DefaultEnabledGuardian();
        g.Enabled.Should().BeTrue();
    }

    // Minimal guardian that does NOT declare Enabled (exercises the default member).
    private sealed class DefaultEnabledGuardian : IGuardian
    {
        public string Name => "default";
        public GuardianCategory Category => GuardianCategory.Data;
        public TimeSpan Interval => TimeSpan.FromMinutes(1);
        public Task<GuardianReport> GuardAsync(CancellationToken ct = default) =>
            Task.FromResult(GuardianReport.Ok(this, 0));
    }

    // --- GuardianConfig.Read ---

    [Fact]
    public void GuardianConfig_Read_returns_defaults_when_no_config_present()
    {
        var config = new ConfigurationBuilder().Build();
        var defaultInterval = TimeSpan.FromMinutes(30);

        var (enabled, interval) = GuardianConfig.Read(config, "MyGuardian", defaultInterval);

        enabled.Should().BeTrue("Enabled defaults to true when absent");
        interval.Should().Be(defaultInterval, "Interval defaults to the supplied default when absent");
    }

    [Fact]
    public void GuardianConfig_Read_returns_configured_values()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:MyGuardian:Enabled"] = "false",
                ["BotDetection:Guardians:MyGuardian:Interval"] = "01:00:00"
            })
            .Build();
        var defaultInterval = TimeSpan.FromMinutes(30);

        var (enabled, interval) = GuardianConfig.Read(config, "MyGuardian", defaultInterval);

        enabled.Should().BeFalse("config said false");
        interval.Should().Be(TimeSpan.FromHours(1), "config said 01:00:00");
    }

    [Fact]
    public void GuardianConfig_Read_applies_default_when_only_Enabled_is_set()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:AGuardian:Enabled"] = "false"
            })
            .Build();
        var defaultInterval = TimeSpan.FromHours(6);

        var (enabled, interval) = GuardianConfig.Read(config, "AGuardian", defaultInterval);

        enabled.Should().BeFalse();
        interval.Should().Be(defaultInterval, "Interval falls back to the default when not in config");
    }
}
