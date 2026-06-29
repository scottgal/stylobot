using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.PrometheusPack.Extensions;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Scheduling;

/// <summary>
///     Regression tests for the dashboard / PrometheusPack DI plumbing around
///     <see cref="IScheduleCoordinator"/>. Three invariants the production
///     code MUST satisfy and historically did not:
///     <list type="number">
///         <item>
///             <description>
///                 <c>AddStyloBotDashboard</c> registers the real
///                 <see cref="ScheduleCoordinator"/> with an
///                 <see cref="IHostedService"/> entry -- not the
///                 <see cref="NullScheduleCoordinator"/> sentinel that silently
///                 makes every <c>Subscribe(...)</c> a no-op.
///             </description>
///         </item>
///         <item>
///             <description>
///                 When a host registers <see cref="NullScheduleCoordinator"/>
///                 first and later calls <c>AddPrometheusPack</c>, the pack
///                 upgrades the sentinel to the real coordinator. The previous
///                 "anything already registered? skip" guard let the sentinel
///                 win silently.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="NullScheduleCoordinator.Subscribe"/> emits a
///                 warning when called against the null sentinel, so a future
///                 shadow-registration is observable instead of zero-data.
///             </description>
///         </item>
///     </list>
/// </summary>
public sealed class ScheduleCoordinatorRegistrationTests
{
    [Fact]
    public async Task Dashboard_DI_does_not_shadow_real_ScheduleCoordinator()
    {
        var services = NewBaseServices();
        services.AddStyloBotDashboard();

        // ScheduleCoordinator implements IAsyncDisposable only, so the provider
        // must dispose async.
        await using var sp = services.BuildServiceProvider();
        var coord = sp.GetRequiredService<IScheduleCoordinator>();

        coord.Should().NotBeSameAs(NullScheduleCoordinator.Instance,
            "dashboard DI MUST NOT register the null sentinel -- it makes every Subscribe(...) a no-op");
        coord.Should().BeOfType<ScheduleCoordinator>(
            "dashboard DI must register the real coordinator so Tick10s + Tick1m actually fire");

        services.Should().Contain(sd =>
                sd.ServiceType == typeof(IHostedService)
                && sd.ImplementationFactory != null
                && sd.Lifetime == ServiceLifetime.Singleton,
            "ScheduleCoordinator must be registered as IHostedService so StartAsync runs the cadence loops");
    }

    [Fact]
    public async Task AddPrometheusPack_after_null_sentinel_upgrades_to_real_coordinator()
    {
        var services = NewBaseServices();
        // Simulate a host that registers the null sentinel before the pack
        // (the historical bug: StyloBotDashboardServiceExtensions did exactly
        // this, then EnsureScheduleCoordinatorRegistered short-circuited).
        services.AddSingleton<IScheduleCoordinator>(NullScheduleCoordinator.Instance);

        services.AddPrometheusPack(opt =>
        {
            opt.Mode = PrometheusPackMode.Remote;
            opt.Remote = ro => { ro.BaseUrl = "http://gw.test:8080"; };
        });

        await using var sp = services.BuildServiceProvider();
        var coord = sp.GetRequiredService<IScheduleCoordinator>();

        coord.Should().BeOfType<ScheduleCoordinator>(
            "EnsureScheduleCoordinatorRegistered must replace the null sentinel with the real coordinator");
    }

    [Fact]
    public void NullScheduleCoordinator_logs_warning_when_subscribed()
    {
        // Static state on the sentinel persists across tests in the same
        // process. Reset both the warn sink and the "already warned about
        // this cadence" memo BEFORE attaching the FakeLogger so test order
        // can't consume the one-shot warning slot for us.
        NullScheduleCoordinator.SetWarnSink(null);
        NullScheduleCoordinator.ResetWarnedCadencesForTesting();

        var collector = new FakeLogCollector();
        var logger = new FakeLogger<NullScheduleCoordinator>(collector);
        NullScheduleCoordinator.SetWarnSink(logger);
        try
        {
            using var _ = NullScheduleCoordinator.Instance.Subscribe(
                TickCadence.Tick10s,
                "test-subscriber",
                CostHint.Low,
                (_, _) => Task.CompletedTask);

            var records = collector.GetSnapshot();
            records.Should().Contain(r =>
                    r.Level == LogLevel.Warning
                    && r.Message.Contains("NullScheduleCoordinator"),
                "Subscribe(...) on the null sentinel must warn so a future shadow-registration is observable");
        }
        finally
        {
            // Detach the FakeLogger so the static instance doesn't leak it
            // out of this test's scope.
            NullScheduleCoordinator.SetWarnSink(null);
            NullScheduleCoordinator.ResetWarnedCadencesForTesting();
        }
    }

    // ---- helpers -------------------------------------------------------------

    private static IServiceCollection NewBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // AddStyloBotDashboard reaches IOptions<BotDetectionOptions> indirectly
        // (for the SQLite connection string). Register an empty options instance
        // so BuildServiceProvider doesn't choke on the factory path.
        services.AddOptions<BotDetectionOptions>();
        return services;
    }
}