using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.HealthSummaryProviders;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.E2E;

/// <summary>
///     The optional-pack contract (issue #124 + 2026-08-26 design steering):
///     Prometheus is an OPTIONAL add-on to the dashboard, not a hard dependency.
///     The meter-health tile lives in the Prometheus pack and is registered by
///     <c>AddPrometheusPack</c> through the UI's <see cref="IPackHealthSummaryProvider"/>
///     seam. A host that never installs the pack must boot with a fully-working
///     dashboard (no meter tile, no crash); a host that adds the pack gets the tile.
/// </summary>
public sealed class PrometheusOptionalityTests
{
    private static IServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllersWithViews();
        // Bare ServiceCollection (no host) -> supply the configuration the
        // dashboard / pack bind from (e.g. ScheduleCoordinatorOptions).
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        return services;
    }

    private static void ConfigureDashboard(IServiceCollection services)
        => services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

    [Fact]
    public async Task Host_without_prometheus_pack_boots_without_the_meter_tile()
    {
        var services = BaseServices();
        ConfigureDashboard(services);

        await using var sp = services.BuildServiceProvider();

        sp.GetService<IMeterStream>().Should().BeNull(
            "no AddPrometheusPack was called, so no meter stream may exist.");

        var providers = sp.GetServices<IPackHealthSummaryProvider>();
        providers.Should().NotContain(p => p.GetType() == typeof(MeterStreamHealthSummaryProvider),
            "the meter tile is pack-owned; a host without the pack must not have it registered.");
    }

    [Fact]
    public async Task Host_with_prometheus_pack_registers_the_meter_tile()
    {
        var services = BaseServices();
        services.AddPrometheusPack(opt => opt.Mode = PrometheusPackMode.Local);
        ConfigureDashboard(services);

        await using var sp = services.BuildServiceProvider();

        sp.GetService<IMeterStream>().Should().NotBeNull(
            "AddPrometheusPack registers the meter stream.");
        sp.GetService<Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.MeterStreamHealthTileCache>()
            .Should().NotBeNull("the pack registers the meter tile's shingle cache.");

        var providers = sp.GetServices<IPackHealthSummaryProvider>();
        providers.Should().Contain(p => p.GetType() == typeof(MeterStreamHealthSummaryProvider),
            "AddPrometheusPack registers its meter-health tile through the UI's pack seam.");
    }
}
