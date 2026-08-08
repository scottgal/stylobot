using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

/// <summary>
///     Pins the task-#65 signal-emission pattern for
///     <see cref="ThreatIntelRefreshService"/>: a successful provider refresh
///     raises <see cref="ThreatIntelRefreshedSignal"/> naming the provider,
///     a failure does not, and a recovery latches
///     <see cref="ThreatIntelRefreshedSignal.RecoveredFromFailure"/>.
/// </summary>
public class ThreatIntelRefreshSignalTests
{
    private static TypedSignalSink<ThreatIntelRefreshedSignal> NewSink()
    {
        var inner = new SignalSink(maxCapacity: 16, maxAge: TimeSpan.FromMinutes(5));
        return new TypedSignalSink<ThreatIntelRefreshedSignal>(inner);
    }

    private static ThreatIntelRefreshService NewService(
        IThreatIntelCoordinator coordinator,
        TypedSignalSink<ThreatIntelRefreshedSignal>? refreshSignals = null,
        bool blockStartupOnFirstFetch = false,
        RecordingScheduleCoordinator? scheduleCoordinator = null)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            ThreatIntel = new ThreatIntelOptions
            {
                Enabled = true,
                BlockStartupOnFirstFetch = blockStartupOnFirstFetch,
                StartupFetchTimeoutSeconds = 5,
                StaggerWindowSeconds = 0,
            },
        });
        return new ThreatIntelRefreshService(
            coordinator,
            options,
            NullLogger<ThreatIntelRefreshService>.Instance,
            scheduleCoordinator ?? new RecordingScheduleCoordinator(),
            refreshSignals: refreshSignals);
    }

    [Fact]
    public async Task Successful_bootstrap_raises_refreshed_signal_per_provider()
    {
        var sink = NewSink();
        var received = new List<ThreatIntelRefreshedSignal>();
        var received_lock = new object();
        sink.TypedSignalRaised += evt =>
        {
            lock (received_lock) received.Add(evt.Payload);
        };

        var a = new SuccessfulProvider("alpha");
        var b = new SuccessfulProvider("beta");
        var coordinator = new StubCoordinator(a, b);
        using var service = NewService(coordinator, sink, blockStartupOnFirstFetch: true);

        await service.StartAsync(CancellationToken.None);
        // Bound the steady-state loop before its first tick can complicate the
        // assertion. BlockStartupOnFirstFetch=true awaits the bootstrap refresh
        // for each provider, so both raises are guaranteed to have landed by
        // the time StartAsync returns.
        await service.StopAsync(CancellationToken.None);

        List<ThreatIntelRefreshedSignal> snapshot;
        lock (received_lock) snapshot = received.ToList();

        snapshot.Select(r => r.Provider).Should().Contain(new[] { "alpha", "beta" },
            "each configured provider raises a refreshed signal on successful bootstrap");
        snapshot.All(r => !r.RecoveredFromFailure).Should().BeTrue(
            "first-attempt bootstrap success has no prior failure to recover from");
    }

    [Fact]
    public async Task Failed_bootstrap_does_not_raise_signal()
    {
        var sink = NewSink();
        var received = new List<ThreatIntelRefreshedSignal>();
        sink.TypedSignalRaised += evt => received.Add(evt.Payload);

        var failing = new FailingProvider("bad");
        var coordinator = new StubCoordinator(failing);
        // Non-blocking mode so the service logs + continues rather than throwing.
        using var service = NewService(coordinator, sink, blockStartupOnFirstFetch: false);

        await service.StartAsync(CancellationToken.None);

        received.Should().BeEmpty(
            "no signal must fire when the refresh never lands -- consumers " +
            "would react to a failed fetch as if the cache had been populated");
    }

    [Fact]
    public async Task No_sink_configured_does_not_throw()
    {
        var provider = new SuccessfulProvider("solo");
        var coordinator = new StubCoordinator(provider);
        using var service = NewService(coordinator, refreshSignals: null, blockStartupOnFirstFetch: true);

        await service.StartAsync(CancellationToken.None);
        // No assertions beyond "no exception".
    }

    [Fact]
    public async Task StartAsync_subscribes_each_offline_provider_to_Tick5m()
    {
        var a = new SuccessfulProvider("alpha");
        var b = new SuccessfulProvider("beta");
        var coordinator = new StubCoordinator(a, b);
        var scheduleCoordinator = new RecordingScheduleCoordinator();
        using var service = NewService(coordinator, scheduleCoordinator: scheduleCoordinator, blockStartupOnFirstFetch: true);

        await service.StartAsync(CancellationToken.None);

        var subs = scheduleCoordinator.Subscriptions;
        subs.Should().HaveCount(2, "each offline provider gets its own subscription, not one shared loop");
        subs.Select(s => s.Cadence).Should().AllBeEquivalentTo(Mostlylucid.Common.Scheduling.TickCadence.Tick5m);
        subs.Select(s => s.Name).Should().Contain(new[]
        {
            $"{nameof(ThreatIntelRefreshService)}:alpha",
            $"{nameof(ThreatIntelRefreshService)}:beta",
        });
    }

    [Fact]
    public async Task Tick_before_RefreshInterval_elapsed_does_not_refresh_again()
    {
        var provider = new CountingProvider("alpha", TimeSpan.FromHours(1));
        var coordinator = new StubCoordinator(provider);
        var scheduleCoordinator = new RecordingScheduleCoordinator();
        // Bootstrap already counts as the first attempt.
        using var service = NewService(coordinator, scheduleCoordinator: scheduleCoordinator, blockStartupOnFirstFetch: true);
        await service.StartAsync(CancellationToken.None);

        provider.RefreshCount.Should().Be(1, "bootstrap performs the first refresh");

        var sub = scheduleCoordinator.Subscriptions.Single();
        await sub.Handler(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        provider.RefreshCount.Should().Be(1,
            "RefreshInterval is 1h; a tick 5 minutes after bootstrap must not trigger another fetch");
    }

    [Fact]
    public async Task Tick_after_RefreshInterval_elapsed_refreshes_again()
    {
        var provider = new CountingProvider("alpha", TimeSpan.FromMinutes(1));
        var coordinator = new StubCoordinator(provider);
        var scheduleCoordinator = new RecordingScheduleCoordinator();
        using var service = NewService(coordinator, scheduleCoordinator: scheduleCoordinator, blockStartupOnFirstFetch: true);
        await service.StartAsync(CancellationToken.None);

        provider.RefreshCount.Should().Be(1);

        var sub = scheduleCoordinator.Subscriptions.Single();
        await sub.Handler(DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);

        provider.RefreshCount.Should().Be(2,
            "RefreshInterval is 1min; a tick 5 minutes later is well past due");
    }

    // ── Fakes ─────────────────────────────────────────────────────────

    private sealed class StubCoordinator : IThreatIntelCoordinator
    {
        public StubCoordinator(params IThreatIntelProvider[] providers)
        {
            Providers = providers;
        }

        public bool IsEnabled => Providers.Count > 0;
        public IReadOnlyList<IThreatIntelProvider> Providers { get; }
        public IReadOnlyList<ThreatIntelVerdict> Lookup(ThreatSubject subject) => Array.Empty<ThreatIntelVerdict>();
        public Task EnrichAsync(ThreatSubject subject, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SuccessfulProvider : IThreatIntelProvider
    {
        public SuccessfulProvider(string name) { Name = name; }
        public string Name { get; }
        public ThreatIntelMode Mode => ThreatIntelMode.Offline;
        public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
        public ThreatIntelVerdict? TryLookup(ThreatSubject subject) => null;
        public Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken) => Task.CompletedTask;
        public ProviderStatus GetStatus() => new()
        {
            Provider = Name,
            Mode = Mode,
            Enabled = true,
            RefreshInterval = RefreshInterval,
        };
    }

    private sealed class CountingProvider : IThreatIntelProvider
    {
        public CountingProvider(string name, TimeSpan refreshInterval) { Name = name; RefreshInterval = refreshInterval; }
        public string Name { get; }
        public ThreatIntelMode Mode => ThreatIntelMode.Offline;
        public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
        public TimeSpan RefreshInterval { get; }
        public int RefreshCount { get; private set; }
        public ThreatIntelVerdict? TryLookup(ThreatSubject subject) => null;
        public Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }
        public ProviderStatus GetStatus() => new()
        {
            Provider = Name,
            Mode = Mode,
            Enabled = true,
            RefreshInterval = RefreshInterval,
        };
    }

    private sealed class FailingProvider : IThreatIntelProvider
    {
        public FailingProvider(string name) { Name = name; }
        public string Name { get; }
        public ThreatIntelMode Mode => ThreatIntelMode.Offline;
        public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } = new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };
        public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
        public ThreatIntelVerdict? TryLookup(ThreatSubject subject) => null;
        public Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
            => throw new InvalidOperationException("simulated feed failure");
        public ProviderStatus GetStatus() => new()
        {
            Provider = Name,
            Mode = Mode,
            Enabled = true,
            RefreshInterval = RefreshInterval,
            LastRefreshFailed = true,
        };
    }
}