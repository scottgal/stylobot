using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
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
        bool blockStartupOnFirstFetch = false)
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