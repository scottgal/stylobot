using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

/// <summary>
///     Wave 2 Category B regression coverage for
///     <see cref="ThreatIntelEnrichmentService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with an
///     <c>await foreach</c> on <see cref="ThreatIntelEnrichmentQueue.Reader"/>;
///     now subscribes to <see cref="TickCadence.Tick10s"/> via
///     <see cref="IScheduleCoordinator"/> and each tick drains whatever subjects
///     landed since the last tick through
///     <see cref="IThreatIntelCoordinator.EnrichAsync"/>.
///     <para>
///         Four facts: subscription shape, tick runs against empty queue,
///         dispose releases the subscription, tick drains the queue.
///     </para>
/// </summary>
public sealed class ThreatIntelEnrichmentServiceTickTests
{
    private static BotDetectionOptions DefaultOptions() => new()
    {
        ThreatIntel = new ThreatIntelOptions
        {
            EnrichmentQueueCapacity = 100,
            EnrichmentTimeoutSeconds = 5
        }
    };

    private static ThreatIntelEnrichmentService NewService(
        RecordingScheduleCoordinator coordinator,
        out ThreatIntelEnrichmentQueue queue,
        out RecordingThreatIntelCoordinator intelCoordinator,
        BotDetectionOptions? options = null,
        bool isEnabled = true,
        bool hasLiveProvider = true)
    {
        var opts = Options.Create(options ?? DefaultOptions());
        queue = new ThreatIntelEnrichmentQueue(opts);
        intelCoordinator = new RecordingThreatIntelCoordinator(isEnabled, hasLiveProvider);
        return new ThreatIntelEnrichmentService(
            queue,
            intelCoordinator,
            opts,
            NullLogger<ThreatIntelEnrichmentService>.Instance,
            coordinator);
    }

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, out _, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("ThreatIntelEnrichmentService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_against_empty_queue()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, out var queue, out var intel);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        queue.Depth.Should().Be(0);
        intel.Enriched.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = NewService(coordinator, out _, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task OnTickAsync_drains_enrichment_queue()
    {
        // Enqueue two subjects BEFORE the tick fires. The migrated service no
        // longer runs an await-foreach, so without a tick fire these subjects
        // sit in the queue waiting for the next drain.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, out var queue, out var intel);

        queue.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, "203.0.113.10")).Should().BeTrue();
        queue.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, "203.0.113.11")).Should().BeTrue();
        queue.Depth.Should().Be(2);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the queue has been drained by the tick
        // handler. Each subject was routed through EnrichAsync.
        queue.Depth.Should().Be(0);
        intel.Enriched.Should().HaveCount(2);
        intel.Enriched.Select(s => s.Value).Should().BeEquivalentTo(["203.0.113.10", "203.0.113.11"]);
    }

    [Fact]
    public async Task OnTickAsync_short_circuits_when_coordinator_disabled()
    {
        // When the threat-intel coordinator is globally disabled, the tick
        // handler early-returns without draining the queue. Subjects accumulate
        // until the operator opts in; on the bounded DropOldest channel they
        // expire silently rather than leaking memory.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, out var queue, out var intel, isEnabled: false);

        queue.TryEnqueue(new ThreatSubject(ThreatSubjectType.Ip, "203.0.113.10")).Should().BeTrue();

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Disabled => tick early-returns => queue is NOT drained, EnrichAsync
        // never called.
        queue.Depth.Should().Be(1);
        intel.Enriched.Should().BeEmpty();
    }

    /// <summary>
    ///     Minimal <see cref="IThreatIntelCoordinator"/> stand-in: records each
    ///     <see cref="EnrichAsync"/> invocation so tests can assert which
    ///     subjects flowed through the tick handler's drain pass.
    /// </summary>
    private sealed class RecordingThreatIntelCoordinator : IThreatIntelCoordinator
    {
        private readonly List<IThreatIntelProvider> _providers;
        public List<ThreatSubject> Enriched { get; } = new();

        public RecordingThreatIntelCoordinator(bool isEnabled, bool hasLiveProvider)
        {
            IsEnabled = isEnabled;
            _providers = hasLiveProvider
                ? new List<IThreatIntelProvider> { new StubLiveProvider() }
                : new List<IThreatIntelProvider>();
        }

        public bool IsEnabled { get; }

        public IReadOnlyList<ThreatIntelVerdict> Lookup(ThreatSubject subject) => Array.Empty<ThreatIntelVerdict>();

        public Task EnrichAsync(ThreatSubject subject, CancellationToken cancellationToken)
        {
            Enriched.Add(subject);
            return Task.CompletedTask;
        }

        public IReadOnlyList<IThreatIntelProvider> Providers => _providers;

        private sealed class StubLiveProvider : IThreatIntelProvider
        {
            public string Name => "stub-live";
            public ThreatIntelMode Mode => ThreatIntelMode.Live;
            public IReadOnlySet<ThreatSubjectType> SupportedSubjects { get; } =
                new HashSet<ThreatSubjectType> { ThreatSubjectType.Ip };

            public ThreatIntelVerdict? TryLookup(ThreatSubject subject) => null;
            public Task RefreshAsync(ThreatSubject? subject, CancellationToken cancellationToken)
                => Task.CompletedTask;
            public TimeSpan RefreshInterval => TimeSpan.FromHours(1);
            public ProviderStatus GetStatus() => new() { Provider = Name, Mode = Mode, Enabled = true };
        }
    }
}