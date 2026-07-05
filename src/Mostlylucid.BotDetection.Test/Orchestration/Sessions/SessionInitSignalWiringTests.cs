using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using StyloFlow.Orchestration;

namespace Mostlylucid.BotDetection.Test.Orchestration.Sessions;

/// <summary>
///     Pins the wiring between <see cref="SessionStore.Changes"/>'s first
///     mutation and <see cref="IInitSignalBus"/>: the first <c>Upsert</c>
///     fires <see cref="SessionStoreOptions.InitSignal"/>, which lazy-boots
///     <see cref="SessionAtom"/> + <see cref="SessionPersistenceAtom"/> via
///     <c>AddOnInitSignal&lt;T&gt;</c>. Isolated from the broader
///     BotDetection DI graph so a future refactor cannot silently defeat
///     the lazy-boot property.
/// </summary>
public class SessionInitSignalWiringTests
{
    private static IServiceCollection NewMinimalServices()
    {
        var services = new ServiceCollection();
        services.AddOptions<SessionStoreOptions>().Configure(o =>
        {
            o.CleanupInterval = TimeSpan.FromHours(1); // suppress cleanup loop noise
        });
        services.AddInitSignalBus();
        services.AddLogging();
        services.AddSingleton<SessionStore>();
        return services;
    }

    private static SessionSample NewSample(string fingerprintId = "fp-1", string siteId = "site-1")
        => new()
        {
            FingerprintId = fingerprintId,
            SiteId = siteId,
            Timestamp = DateTimeOffset.UtcNow,
            BotProbability = 0.7,
            Confidence = 0.6,
            StatusCode = 200,
            FromUpstream = true,
            Honeypot = false,
        };

    [Fact]
    public void Init_signal_does_not_fire_before_any_upsert()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        _ = sp.GetRequiredService<SessionStore>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        bus.HasFired(SessionStoreOptions.InitSignal).Should().BeFalse(
            "resolving the store must not fire the init signal -- only an actual Upsert does");
    }

    [Fact]
    public void First_upsert_fires_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var store = sp.GetRequiredService<SessionStore>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        store.Upsert(NewSample());

        bus.HasFired(SessionStoreOptions.InitSignal).Should().BeTrue();
    }

    [Fact]
    public void Subsequent_upserts_do_not_re_fire_init_signal()
    {
        var sp = NewMinimalServices().BuildServiceProvider();
        var store = sp.GetRequiredService<SessionStore>();
        var bus = sp.GetRequiredService<IInitSignalBus>();

        var handlerInvocations = 0;
        using var _ = bus.Subscribe(SessionStoreOptions.InitSignal, () => handlerInvocations++);

        store.Upsert(NewSample(fingerprintId: "fp-1"));
        store.Upsert(NewSample(fingerprintId: "fp-2"));
        store.Upsert(NewSample(fingerprintId: "fp-3"));

        handlerInvocations.Should().Be(1,
            "the first-mutation hook uses CompareExchange + the bus is idempotent; " +
            "the handler must run exactly once regardless of how many aggregates land");
    }

    [Fact]
    public async Task AddOnInitSignal_defers_atom_construction_until_first_upsert()
    {
        var services = NewMinimalServices();
        services.AddOnInitSignal<ProbeAtom>(SessionStoreOptions.InitSignal);
        var sp = services.BuildServiceProvider();

        var hostedServices = sp.GetServices<IHostedService>().ToArray();
        foreach (var hs in hostedServices)
            await hs.StartAsync(CancellationToken.None);

        ProbeAtom.ConstructedCount.Should().Be(0,
            "no upsert has happened; atom must remain dormant");

        var store = sp.GetRequiredService<SessionStore>();
        store.Upsert(NewSample());

        ProbeAtom.ConstructedCount.Should().Be(1);

        // Second upsert must not reconstruct.
        store.Upsert(NewSample(fingerprintId: "fp-2"));
        ProbeAtom.ConstructedCount.Should().Be(1);

        foreach (var hs in hostedServices)
            await hs.StopAsync(CancellationToken.None);
    }

    private sealed class ProbeAtom
    {
        private static int s_constructedCount;
        public static int ConstructedCount => s_constructedCount;
        public ProbeAtom() => Interlocked.Increment(ref s_constructedCount);
        public static void Reset() => Interlocked.Exchange(ref s_constructedCount, 0);
    }

    public SessionInitSignalWiringTests()
    {
        ProbeAtom.Reset();
    }
}
