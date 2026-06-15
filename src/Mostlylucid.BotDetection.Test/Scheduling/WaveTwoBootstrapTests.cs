using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;

namespace Mostlylucid.BotDetection.Test.Scheduling;

/// <summary>
///     Wave 2 architectural-drift integration test: verifies the migrated
///     PrometheusPack singletons actually subscribe to the
///     <see cref="IScheduleCoordinator"/> when registered via DI + brought up
///     by the bootstrap shim.
/// </summary>
public sealed class WaveTwoBootstrapTests
{
    [Fact]
    public async Task Local_pack_bootstrap_registers_subscriptions_on_coordinator()
    {
        var services = NewServices(out var coordinator);
        services.AddPrometheusPack(opt => { opt.Mode = PrometheusPackMode.Local; });
        await using var sp = services.BuildServiceProvider();

        // The bootstrap shim does the eager resolution that fires Subscribe(...).
        await StartHostedServicesAsync(sp);

        var subscribers = coordinator.Snapshot();
        subscribers.Should().Contain(s => s.SubscriberName == "MeterTriggerService" && s.Cadence == TickCadence.Tick1s);
        subscribers.Should().Contain(s => s.SubscriberName == "LocalMeterStream.Decay" && s.Cadence == TickCadence.Tick1m);
    }

    [Fact]
    public async Task Remote_pack_bootstrap_registers_poll_and_decay_subscriptions()
    {
        var services = NewServices(out var coordinator);
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro => { ro.BaseUrl = "http://gw.test:8080"; };
        });
        await using var sp = services.BuildServiceProvider();

        await StartHostedServicesAsync(sp);

        var subscribers = coordinator.Snapshot();
        subscribers.Should().Contain(s => s.SubscriberName == "RemoteMeterStream.Poll" && s.Cadence == TickCadence.Tick10s);
        subscribers.Should().Contain(s => s.SubscriberName == "RemoteMeterStream.Decay" && s.Cadence == TickCadence.Tick1m);
        subscribers.Should().Contain(s => s.SubscriberName == "MeterTriggerService" && s.Cadence == TickCadence.Tick1s);
    }

    [Fact]
    public async Task Remote_pack_honours_custom_PollCadence()
    {
        var services = NewServices(out var coordinator);
        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro =>
            {
                ro.BaseUrl = "http://gw.test:8080";
                ro.PollCadence = TickCadence.Tick1s;
            };
        });
        await using var sp = services.BuildServiceProvider();

        await StartHostedServicesAsync(sp);

        var subscribers = coordinator.Snapshot();
        subscribers.Should().Contain(s => s.SubscriberName == "RemoteMeterStream.Poll" && s.Cadence == TickCadence.Tick1s);
    }

    // ---- helpers -------------------------------------------------------------

    private static IServiceCollection NewServices(out RecordingScheduleCoordinator coordinator)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        // Record-the-subscriptions coordinator: NOT NullScheduleCoordinator
        // because that one doesn't track subscriptions (the whole point of this
        // test is to assert Subscribe(...) was called with the right names).
        coordinator = new RecordingScheduleCoordinator();
        var captured = coordinator;
        services.AddSingleton<IScheduleCoordinator>(captured);
        return services;
    }

    private static async Task StartHostedServicesAsync(IServiceProvider sp)
    {
        // PrometheusPackBootstrap is the IHostedService that does the eager
        // resolution. Running its StartAsync is what makes the migrated
        // singletons construct + subscribe.
        foreach (var hosted in sp.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }
    }

}
