using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Telemetry;

public class PrometheusPackDiModeSwitchTests
{
    private static IServiceCollection NewServices()
    {
        // RemoteMeterStream's options builder calls .BindConfiguration(...),
        // which resolves IConfiguration off the container. Empty config is
        // fine -- the binder is a no-op when the section is missing, and the
        // caller-supplied Action<RemoteMeterStreamOptions> runs after the bind.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // Wave 2: the migrated streams take IScheduleCoordinator. These DI
        // mode-switch tests don't drive any ticks, so the null coordinator is
        // the right test double -- the production AddBotDetection extension
        // registers the real one in non-test composition.
        services.AddSingleton<IScheduleCoordinator>(NullScheduleCoordinator.Instance);
        return services;
    }

    [Fact]
    public void Local_mode_registers_LocalMeterStream_as_IMeterStream()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; });
        using var sp = services.BuildServiceProvider();
        var stream = sp.GetRequiredService<IMeterStream>();
        Assert.IsType<LocalMeterStream>(stream);
    }

    [Fact]
    public void Local_mode_invokes_caller_configure_callback()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Local;
            opt.Local = lo =>
            {
                lo.MaxTrackedMeters = 17;
                lo.MeterNamePrefixFilter = "z";
            };
        });
        using var sp = services.BuildServiceProvider();
        var bound = sp.GetRequiredService<IOptions<LocalMeterStreamOptions>>().Value;
        Assert.Equal(17, bound.MaxTrackedMeters);
        Assert.Equal("z", bound.MeterNamePrefixFilter);
    }

    [Fact]
    public void Remote_mode_registers_RemoteMeterStream_as_IMeterStream()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro => { ro.BaseUrl = "http://gateway.lan:8080"; };
        });
        using var sp = services.BuildServiceProvider();
        var stream = sp.GetRequiredService<IMeterStream>();
        Assert.IsType<RemoteMeterStream>(stream);
    }

    [Fact]
    public void Remote_mode_without_configuration_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleCoordinator>(NullScheduleCoordinator.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Remote; }));
        Assert.Contains("Remote configuration", ex.Message);
    }

    [Fact]
    public void Mode_local_with_remote_config_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleCoordinator>(NullScheduleCoordinator.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddPrometheusPack(opt =>
            {
                opt.Mode = PrometheusPackMode.Local;
                opt.Remote = ro => { ro.BaseUrl = "x"; };
            }));
        Assert.Contains("Mode is Local but Remote configuration", ex.Message);
    }

    [Fact]
    public void Mode_remote_with_local_config_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IScheduleCoordinator>(NullScheduleCoordinator.Instance);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddPrometheusPack(opt =>
            {
                opt.Mode = PrometheusPackMode.Remote;
                opt.Remote = ro => { ro.BaseUrl = "x"; };
                opt.Local = lo => { lo.MaxTrackedMeters = 1; };
            }));
        Assert.Contains("Mode is Remote but Local configuration", ex.Message);
    }

    [Fact]
    public void Double_registration_throws()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; }));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Local_mode_registers_eager_bootstrap_hosted_service()
    {
        // Wave 2: LocalMeterStream no longer inherits IHostedService. Instead
        // AddPrometheusPack registers a PrometheusPackBootstrap shim whose only
        // job is to RESOLVE the singleton at boot so its constructor's
        // Subscribe(...) fires against the ScheduleCoordinator.
        var services = NewServices();
        services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; });
        using var sp = services.BuildServiceProvider();

        var hosted = sp.GetServices<IHostedService>().ToList();
        // The bootstrap shim is internal; assert by type-name suffix to keep
        // the test free of an InternalsVisibleTo round-trip.
        Assert.Contains(hosted, h => h.GetType().Name == "PrometheusPackBootstrap");
        // LocalMeterStream is no longer an IHostedService -- assertion lives in
        // type-name space so it survives the inheritance drop.
        Assert.DoesNotContain(hosted, h => h.GetType() == typeof(LocalMeterStream));
    }

    [Fact]
    public void Remote_mode_registers_eager_bootstrap_hosted_service()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro => { ro.BaseUrl = "http://gateway.lan:8080"; };
        });
        using var sp = services.BuildServiceProvider();

        var hosted = sp.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, h => h.GetType().Name == "PrometheusPackBootstrap");
        Assert.DoesNotContain(hosted, h => h.GetType() == typeof(RemoteMeterStream));
    }
}
