using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

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
        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Remote; }));
        Assert.Contains("Remote configuration", ex.Message);
    }

    [Fact]
    public void Mode_local_with_remote_config_throws()
    {
        var services = new ServiceCollection();
        services.AddLogging();
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
    public void Local_mode_registers_LocalMeterStream_as_hosted_service()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; });
        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>();
        Assert.Contains(hosted, h => h is LocalMeterStream);
    }

    [Fact]
    public void Remote_mode_registers_RemoteMeterStream_as_hosted_service()
    {
        var services = NewServices();
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro => { ro.BaseUrl = "http://gateway.lan:8080"; };
        });
        using var sp = services.BuildServiceProvider();
        var hosted = sp.GetServices<IHostedService>();
        Assert.Contains(hosted, h => h is RemoteMeterStream);
    }
}
