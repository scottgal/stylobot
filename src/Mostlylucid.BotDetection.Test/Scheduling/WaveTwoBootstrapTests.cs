using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.Scheduling;

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
        using var sp = services.BuildServiceProvider();

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
        using var sp = services.BuildServiceProvider();

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
        using var sp = services.BuildServiceProvider();

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

    /// <summary>
    ///     <see cref="IScheduleCoordinator"/> stand-in that records every
    ///     subscription so the test can assert on names/cadences without
    ///     standing up the real coordinator's cadence loops.
    /// </summary>
    private sealed class RecordingScheduleCoordinator : IScheduleCoordinator
    {
        private readonly List<TickSubscriberMetadata> _subs = new();
        private readonly object _gate = new();

        public IDisposable Subscribe(
            TickCadence cadence,
            string subscriberName,
            CostHint costHint,
            Func<DateTimeOffset, CancellationToken, Task> handler)
        {
            lock (_gate)
            {
                _subs.Add(new TickSubscriberMetadata(cadence, subscriberName, costHint, null, null, 0, 0));
            }
            return new NoopDisposable();
        }

        public IReadOnlyList<TickSubscriberMetadata> Snapshot()
        {
            lock (_gate) return _subs.ToArray();
        }

        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
