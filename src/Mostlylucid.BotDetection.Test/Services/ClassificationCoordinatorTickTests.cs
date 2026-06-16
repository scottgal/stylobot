using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Test.Scheduling.Helpers;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Wave 2 Category B regression coverage for the paired
///     <see cref="LlmClassificationCoordinator"/> +
///     <see cref="IntentClassificationCoordinator"/>. Both were
///     <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>s with
///     unbounded <c>await foreach</c> on their channel readers; now subscribe
///     to <see cref="TickCadence.Tick10s"/> and each tick drains the channel
///     non-blocking via <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>.
///     <para>
///         Four facts per coordinator: subscription shape, tick runs against
///         empty channel, dispose releases the subscription, tick drains the
///         channel. Both services are LLM-bound and process sequentially
///         within the tick; ScheduleCoordinator's single-flight guarantee
///         prevents re-entry while a tick is still draining.
///     </para>
/// </summary>
public sealed class ClassificationCoordinatorTickTests
{
    private static IOptions<BotDetectionOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new BotDetectionOptions
        {
            LlmCoordinator = new LlmCoordinatorOptions
            {
                ChannelCapacity = 100,
                BaseSampleRate = 0.1
            }
        });

    private static LlmClassificationCoordinator NewLlmCoordinator(
        RecordingScheduleCoordinator coordinator,
        out RecordingReputationCache cache)
    {
        cache = new RecordingReputationCache();
        var opts = Options();
        var updater = new PatternReputationUpdater(NullLogger<PatternReputationUpdater>.Instance, opts);
        // ServiceProvider with no LLM provider registered -- the tick handler
        // falls through to the IBotNameSynthesizer fallback (also null in this
        // fixture) and exits cleanly without throwing.
        var services = new ServiceCollection().BuildServiceProvider();
        return new LlmClassificationCoordinator(
            NullLogger<LlmClassificationCoordinator>.Instance,
            services,
            cache,
            updater,
            opts,
            resultCallback: null,
            learningBus: null,
            nameSynthesizer: null,
            scheduleCoordinator: coordinator);
    }

    private static IntentClassificationCoordinator NewIntentCoordinator(
        RecordingScheduleCoordinator coordinator,
        out RecordingIntentSearch intentSearch,
        out RecordingReputationCache cache)
    {
        intentSearch = new RecordingIntentSearch();
        cache = new RecordingReputationCache();
        var vectorizer = new IntentVectorizer();
        var services = new ServiceCollection().BuildServiceProvider();
        return new IntentClassificationCoordinator(
            NullLogger<IntentClassificationCoordinator>.Instance,
            services,
            intentSearch,
            vectorizer,
            cache,
            learningBus: null,
            scheduleCoordinator: coordinator);
    }

    // ---------- LlmClassificationCoordinator ----------

    [Fact]
    public void Llm_constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewLlmCoordinator(coordinator, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("LlmClassificationCoordinator");
        sub.Hint.Should().Be(CostHint.High);
    }

    [Fact]
    public async Task Llm_OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewLlmCoordinator(coordinator, out _);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        sut.QueueDepth.Should().Be(0);
    }

    [Fact]
    public void Llm_dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = NewLlmCoordinator(coordinator, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task Llm_OnTickAsync_drains_classification_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewLlmCoordinator(coordinator, out _);

        sut.TryEnqueue(NewLlmRequest("sig-a")).Should().BeTrue();
        sut.TryEnqueue(NewLlmRequest("sig-b")).Should().BeTrue();
        sut.QueueDepth.Should().Be(2);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the channel has been drained by the
        // tick handler. Without an LLM provider registered, each request
        // falls through to the synthesizer-fallback no-op and counts as
        // processed.
        sut.QueueDepth.Should().Be(0);
        sut.TotalProcessed.Should().Be(2);
    }

    // ---------- IntentClassificationCoordinator ----------

    [Fact]
    public void Intent_constructor_subscribes_to_Tick10s_with_service_name()
    {
        var coordinator = new RecordingScheduleCoordinator();

        using var sut = NewIntentCoordinator(coordinator, out _, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sub.Cadence.Should().Be(TickCadence.Tick10s);
        sub.Name.Should().Be("IntentClassificationCoordinator");
        sub.Hint.Should().Be(CostHint.High);
    }

    [Fact]
    public async Task Intent_OnTickAsync_runs_without_throwing_against_empty_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewIntentCoordinator(coordinator, out var search, out _);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        captured.Disposed.Should().BeFalse();
        sut.QueueDepth.Should().Be(0);
        search.Added.Should().BeEmpty();
    }

    [Fact]
    public void Intent_dispose_unsubscribes_from_coordinator()
    {
        var coordinator = new RecordingScheduleCoordinator();
        var sut = NewIntentCoordinator(coordinator, out _, out _);

        var sub = Assert.Single(coordinator.Subscriptions);
        sut.Dispose();

        sub.Disposed.Should().BeTrue();

        // Double-dispose must be safe.
        sut.Dispose();
    }

    [Fact]
    public async Task Intent_OnTickAsync_drains_classification_channel()
    {
        var coordinator = new RecordingScheduleCoordinator();
        using var sut = NewIntentCoordinator(coordinator, out var search, out _);

        sut.TryEnqueue(NewIntentRequest("sig-a")).Should().BeTrue();
        sut.TryEnqueue(NewIntentRequest("sig-b")).Should().BeTrue();
        sut.QueueDepth.Should().Be(2);

        var captured = Assert.Single(coordinator.Subscriptions);
        await captured.Handler(DateTimeOffset.UtcNow, CancellationToken.None);

        // Cat-B load-bearing assertion: the channel has been drained by the
        // tick handler. With no LearningEventBus registered the fallback path
        // calls IIntentSimilaritySearch.AddAsync for each request.
        sut.QueueDepth.Should().Be(0);
        sut.TotalProcessed.Should().Be(2);
        search.Added.Should().HaveCount(2);
    }

    private static LlmClassificationRequest NewLlmRequest(string signature) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        PrimarySignature = signature,
        UserAgent = "test-ua/1.0",
        PreBuiltRequestInfo = "GET /test",
        HeuristicProbability = 0.5,
        TopReasons = new List<string>(),
        Signals = new Dictionary<string, object>()
    };

    private static IntentClassificationRequest NewIntentRequest(string signature) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        PrimarySignature = signature,
        IntentVector = new float[IntentVectorizer.VectorDimension],
        IntentFeatures = new Dictionary<string, float>(),
        Signals = new Dictionary<string, object>(),
        SessionSummary = "test session",
        HeuristicThreatScore = 0.5
    };

    /// <summary>
    ///     Minimal <see cref="IPatternReputationCache"/> stand-in. The tick
    ///     tests for these coordinators don't assert reputation side-effects
    ///     (the no-LLM path skips most of them); the cache just has to satisfy
    ///     the interface without throwing.
    /// </summary>
    private sealed class RecordingReputationCache : IPatternReputationCache
    {
        private readonly Dictionary<string, PatternReputation> _store = new();

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
            => _store[reputation.PatternId] = reputation;

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
    ///     Minimal <see cref="IIntentSimilaritySearch"/> stand-in. Records each
    ///     <see cref="AddAsync"/> invocation so the drain test can confirm the
    ///     tick handler routed each request through the fallback persistence
    ///     path.
    /// </summary>
    private sealed class RecordingIntentSearch : IIntentSimilaritySearch
    {
        public List<(string SignatureId, double ThreatScore, string Category)> Added { get; } = new();

        public Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(
            float[] vector, int topK = 5, float minSimilarity = 0.75f)
            => Task.FromResult<IReadOnlyList<SimilarIntent>>(Array.Empty<SimilarIntent>());

        public Task AddAsync(float[] vector, string signatureId,
            double threatScore, string intentCategory, string? reasoning = null)
        {
            Added.Add((signatureId, threatScore, intentCategory));
            return Task.CompletedTask;
        }

        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
        public int Count => Added.Count;
    }
}
