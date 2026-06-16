using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 Category B regression coverage for
///     <see cref="BackgroundEnrichmentService"/>. Was a
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///     <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on
///     <c>_channel.Reader.ReadAllAsync</c>; now subscribes to
///     <see cref="TickCadence.Tick10s"/> and each tick drains the channel via
///     <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>,
///     firing per-item processing under the shared concurrency semaphore.
///     <para>
///         Three standard Cat-B facts (subscription shape, tick runs, dispose
///         releases) plus one Cat-B-specific fact: the tick handler drains the
///         channel even when the underlying enrichment lookups are disabled,
///         because the disabled state is gated at <c>TryEnqueue</c>, not at
///         drain time -- so any item that *did* make it onto the channel will
///         flow through the tick handler's drain loop.
///     </para>
/// </summary>
public sealed class BackgroundEnrichmentServiceTickTests
{
    private static BotDetectionOptions DefaultOptions()
    {
        // ProjectHoneypot disabled so TryEnqueue returns false unless we
        // override -- but the channel itself is reachable via reflection-free
        // direct test access on the service. For the drain test, we configure
        // ProjectHoneypot.Enabled = true + a stub key so TryEnqueue lands rows
        // on the channel.
        return new BotDetectionOptions
        {
            BackgroundEnrichment = new BackgroundEnrichmentOptions
            {
                ChannelCapacity = 1_000,
                MaxConcurrency = 4
            },
            ProjectHoneypot = new ProjectHoneypotOptions
            {
                Enabled = true,
                AccessKey = "stub-test-key"
            }
        };
    }

    private static BackgroundEnrichmentService NewService(
        RecordingScheduleCoordinator coordinator,
        out RecordingReputationCache cache,
        BotDetectionOptions? options = null)
    {
        var opts = Options.Create(options ?? DefaultOptions());
        cache = new RecordingReputationCache();
        var honeypot = new ProjectHoneypotLookupService(
            NullLogger<ProjectHoneypotLookupService>.Instance, opts);
        // VerifiedBotRegistry's tick subscription captures one slot on the
        // recording coordinator -- that slot is filtered out in the test
        // assertions by name.
        var verified = new VerifiedBotRegistry(
            NullLogger<VerifiedBotRegistry>.Instance,
            new StubHttpClientFactory(),
            Options.Create(new VerifiedBotRegistryOptions()),
            coordinator);
        var updater = new PatternReputationUpdater(
            NullLogger<PatternReputationUpdater>.Instance, opts);
        return new BackgroundEnrichmentService(
            NullLogger<BackgroundEnrichmentService>.Instance,
            honeypot,
            verified,
            cache,
            updater,
            opts,
            coordinator);
    }

    private static RecordingScheduleCoordinator.Recorded EnrichmentSubscription(
        RecordingScheduleCoordinator coordinator)
        => coordinator.Subscriptions.Single(s => s.Name == "BackgroundEnrichmentService");

    [Fact]
    public void Constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, out _);

        var sub = EnrichmentSubscription(coordinator);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("BackgroundEnrichmentService");
        sub.Hint.Should().Be(CostHint.Medium);
    }

    [Fact]
    public async Task OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewService(coordinator, out _);

        var captured = EnrichmentSubscription(coordinator);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        sut.QueueDepth.Should().Be(0);
    }

    [Fact]
    public void Dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();

        var sut = NewService(coordinator, out _);

        var sub = EnrichmentSubscription(coordinator);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task OnTickAsync_drains_enrichment_channel()
    {
        // Enqueue two requests BEFORE the tick fires. The migrated service no
        // longer runs an inner await-foreach, so without a tick fire these
        // requests sit in the channel waiting for the next drain.
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewService(coordinator, out _);

        sut.TryEnqueue(NewRequest("203.0.113.10")).Should().BeTrue();
        sut.TryEnqueue(NewRequest("203.0.113.11")).Should().BeTrue();

        sut.QueueDepth.Should().Be(2);

        var captured = EnrichmentSubscription(coordinator);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the channel has been drained by the
        // tick handler. The downstream DNS / reputation work happens fire-and-
        // forget under the semaphore (matches the legacy ExecuteAsync shape);
        // the test only asserts the channel is empty after the drain pass.
        sut.QueueDepth.Should().Be(0);
    }

    private static EnrichmentRequest NewRequest(string ip) => new()
    {
        ClientIp = ip,
        SignatureHash = "test-sig",
        BotProbability = 0.7,
        Confidence = 0.6,
        RequestId = Guid.NewGuid().ToString("N"),
        UserAgent = "test-ua/1.0"
    };

    /// <summary>
    ///     Minimal <see cref="IPatternReputationCache"/> stand-in that records
    ///     Update calls so tests can assert downstream side-effects without
    ///     standing up the SQLite persistence layer.
    /// </summary>
    private sealed class RecordingReputationCache : IPatternReputationCache
    {
        private readonly Dictionary<string, PatternReputation> _store = new();
        public List<PatternReputation> Updates { get; } = new();

        public PatternReputation? Get(string patternId)
            => _store.TryGetValue(patternId, out var r) ? r : null;

        public PatternReputation GetOrCreate(string patternId, string patternType, string pattern)
        {
            if (_store.TryGetValue(patternId, out var existing)) return existing;
            var created = new PatternReputation
            {
                PatternId = patternId,
                PatternType = patternType,
                Pattern = pattern
            };
            _store[patternId] = created;
            return created;
        }

        public void Update(PatternReputation reputation)
        {
            _store[reputation.PatternId] = reputation;
            Updates.Add(reputation);
        }

        public IEnumerable<PatternReputation> GetByType(string patternType)
            => _store.Values.Where(r => r.PatternType == patternType).ToList();

        public IEnumerable<PatternReputation> GetByState(ReputationState state)
            => _store.Values.Where(r => r.State == state).ToList();

        public Task DecaySweepAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task GarbageCollectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PersistAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ReputationCacheStats GetStats() => new() { TotalPatterns = _store.Count };
    }

    /// <summary>
    ///     HttpClient factory that returns a 503-stub client. Used by the
    ///     VerifiedBotRegistry dependency so the tick test does no live
    ///     network calls.
    /// </summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(new StubHandler());

        private sealed class StubHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
