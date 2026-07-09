using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Sessions;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Guardians;

/// <summary>
///     Behaviour-preservation tests for <see cref="HnswCompactionGuardian"/>
///     (Phase 3 of the old VectorCompactionService).
///
///     The contract is:
///     <list type="bullet">
///         <item>When <see cref="ISessionVectorSearch"/> is null, <c>GuardAsync</c>
///             returns a no-op <c>Status="ok"</c> report without touching anything.</item>
///         <item>When vector count is at or below <c>HnswLevel1Threshold</c>,
///             no compaction is performed.</item>
///         <item>When vector count exceeds <c>HnswLevel1Threshold</c>, L1 compaction
///             collapses multi-vector signatures to per-signature centroids and calls
///             <see cref="ISessionVectorSearch.ReplaceAllAsync"/>.</item>
///         <item>When vector count still exceeds <c>HnswLevel2Threshold</c> after L1,
///             L2 cluster compaction also runs.</item>
///         <item>The guardian honours the <see cref="IGuardian"/> contract:
///             name, category, interval, and enabled flag.</item>
///     </list>
/// </summary>
public sealed class HnswCompactionGuardianTests
{
    // ---- IGuardian contract ----

    [Fact]
    public void Is_a_data_category_guardian_named_HnswCompaction()
    {
        var sut = Build();

        sut.Should().BeAssignableTo<IGuardian>();
        sut.Name.Should().Be("HnswCompaction");
        sut.Category.Should().Be(GuardianCategory.Data);
    }

    [Fact]
    public void Interval_defaults_to_CompactionInterval_from_options()
    {
        var opts = DefaultOpts();
        opts.Value.Retention.CompactionInterval = TimeSpan.FromMinutes(45);
        var sut = Build(opts: opts);

        sut.Interval.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void Interval_can_be_overridden_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:HnswCompaction:Interval"] = "02:00:00"
            })
            .Build();

        var sut = Build(config: config);

        sut.Interval.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Enabled_defaults_to_true()
    {
        var sut = Build();
        sut.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Enabled_can_be_set_false_via_config()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:Guardians:HnswCompaction:Enabled"] = "false"
            })
            .Build();

        var sut = Build(config: config);
        sut.Enabled.Should().BeFalse();
    }

    // ---- Null vector search: no-op ----

    [Fact]
    public async Task GuardAsync_returns_ok_when_vector_search_is_null()
    {
        // No ISessionVectorSearch registered — HNSW compaction skips gracefully.
        var sut = Build(vectorSearch: null);

        var report = await sut.GuardAsync();

        report.GuardianName.Should().Be("HnswCompaction");
        report.Category.Should().Be(GuardianCategory.Data);
        report.Status.Should().Be("ok");
        report.DurationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    // ---- Below threshold: no compaction ----

    [Fact]
    public async Task GuardAsync_returns_ok_when_count_is_at_or_below_L1_threshold()
    {
        // Set L1 threshold to 10, populate exactly 10 vectors.
        var opts = DefaultOpts();
        opts.Value.Retention.HnswLevel1Threshold = 10;

        var fakeSearch = new FakeVectorSearch(count: 10, snapshot: []);
        var sut = Build(opts: opts, vectorSearch: fakeSearch);

        var report = await sut.GuardAsync();

        report.Status.Should().Be("ok");
        fakeSearch.ReplaceAllCalled.Should().BeFalse("no compaction should run when at or below L1 threshold");
    }

    // ---- Over L1 threshold: L1 compaction runs ----

    [Fact]
    public async Task GuardAsync_runs_L1_compaction_when_count_exceeds_L1_threshold()
    {
        // Two signatures, each with 2 vectors. L1=3 triggers on 4-vector set.
        var opts = DefaultOpts();
        opts.Value.Retention.HnswLevel1Threshold = 3;
        opts.Value.Retention.HnswLevel2Threshold = 100;

        var snapshot = new List<(float[] Vector, SessionVectorMetadata Metadata)>
        {
            (MakeVector(1.0f), MakeMeta("sig-1")),
            (MakeVector(2.0f), MakeMeta("sig-1")),
            (MakeVector(0.5f), MakeMeta("sig-2")),
            (MakeVector(0.6f), MakeMeta("sig-2")),
        };

        var store = new FakeStore([
            MakePriorityInfo("sig-1"),
            MakePriorityInfo("sig-2")
        ]);

        var fakeSearch = new FakeVectorSearch(count: 4, snapshot: snapshot);
        var sut = Build(opts: opts, store: store, vectorSearch: fakeSearch);

        var report = await sut.GuardAsync();

        report.Status.Should().NotBe("error", "the guardian must not throw during L1 compaction");
        fakeSearch.ReplaceAllCalled.Should().BeTrue("ReplaceAllAsync must be called after L1 compaction");

        // After L1: 4 original vectors across 2 sigs must reduce.
        fakeSearch.LastReplaceAllItems!.Count.Should().BeLessThan(4,
            "L1 compaction must reduce the total vector count");
    }

    [Fact]
    public async Task GuardAsync_L1_centroid_has_max_bot_probability_of_source_vectors()
    {
        var opts = DefaultOpts();
        opts.Value.Retention.HnswLevel1Threshold = 1;
        opts.Value.Retention.HnswLevel2Threshold = 100;

        var snapshot = new List<(float[] Vector, SessionVectorMetadata Metadata)>
        {
            (MakeVector(1.0f), MakeMeta("sig-a", botProbability: 0.3)),
            (MakeVector(2.0f), MakeMeta("sig-a", botProbability: 0.8)),
        };

        var store = new FakeStore([MakePriorityInfo("sig-a", botProb: 0.5)]);
        var fakeSearch = new FakeVectorSearch(count: 2, snapshot: snapshot);
        var sut = Build(opts: opts, store: store, vectorSearch: fakeSearch);

        var report = await sut.GuardAsync();

        report.Status.Should().NotBe("error");
        fakeSearch.ReplaceAllCalled.Should().BeTrue();
        fakeSearch.LastReplaceAllItems.Should().HaveCount(1,
            "two vectors for the same sig collapse to one centroid");
        fakeSearch.LastReplaceAllItems![0].Meta.BotProbability.Should().Be(0.8,
            "centroid takes the max bot probability of the source vectors");
        fakeSearch.LastReplaceAllItems[0].Meta.CompressionLevel.Should().Be(1,
            "L1 centroid must have CompressionLevel=1");
    }

    // ---- L2 compaction: low-priority cluster members merged ----

    [Fact]
    public async Task GuardAsync_runs_L2_compaction_when_still_over_L2_threshold_after_L1()
    {
        // Two distinct low-priority signatures in the same cluster, each with 1 vector.
        // L1=1: count(2) > 1 so L1 runs. Each group has count=1 so they're kept at full
        // resolution by L1. After L1 count is still 2. 2 > L2Threshold=1 → L2 runs.
        // Both have priority well below L2CompactionPriorityThreshold=0.5 → merged.
        var opts = DefaultOpts();
        opts.Value.Retention.HnswLevel1Threshold = 1;
        opts.Value.Retention.HnswLevel2Threshold = 1;
        opts.Value.Retention.L2CompactionPriorityThreshold = 0.5;

        const string clusterId = "cluster-A";

        var snapshot = new List<(float[] Vector, SessionVectorMetadata Metadata)>
        {
            (MakeVector(1.0f), MakeMeta("sig-x", clusterId: clusterId)),
            (MakeVector(2.0f), MakeMeta("sig-y", clusterId: clusterId)),
        };

        // botProb=0.01 + Low riskBand → computed Priority = 0.2 * exp(0) * max(0.01,0.1) * 0.5 = 0.01
        // well below L2CompactionPriorityThreshold=0.5
        var store = new FakeStore([
            MakePriorityInfo("sig-x", botProb: 0.01, riskBand: "Low"),
            MakePriorityInfo("sig-y", botProb: 0.01, riskBand: "Low")
        ]);

        var fakeSearch = new FakeVectorSearch(count: 2, snapshot: snapshot);
        var sut = Build(opts: opts, store: store, vectorSearch: fakeSearch);

        var report = await sut.GuardAsync();

        report.Status.Should().NotBe("error");
        fakeSearch.ReplaceAllCalled.Should().BeTrue();

        // After L2: both low-priority cluster members collapsed to one cluster centroid.
        fakeSearch.LastReplaceAllItems.Should().HaveCount(1,
            "two low-priority signatures in the same cluster collapse to one cluster centroid");
        fakeSearch.LastReplaceAllItems![0].Meta.CompressionLevel.Should().Be(2,
            "L2 centroid must have CompressionLevel=2");
        fakeSearch.LastReplaceAllItems[0].Meta.Signature.Should().Be($"cluster:{clusterId}",
            "L2 centroid signature is 'cluster:<clusterId>'");
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static IOptions<BotDetectionOptions> DefaultOpts() =>
        Options.Create(new BotDetectionOptions());

    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().Build();

    private static HnswCompactionGuardian Build(
        IOptions<BotDetectionOptions>? opts = null,
        IConfiguration? config = null,
        IDetectionArchive? store = null,
        ISessionVectorSearch? vectorSearch = null) =>
        new(
            store ?? new FakeStore([]),
            opts ?? DefaultOpts(),
            config ?? EmptyConfig(),
            NullLogger<HnswCompactionGuardian>.Instance,
            vectorSearch);

    private static float[] MakeVector(float fill, int dims = 10)
    {
        var v = new float[dims];
        Array.Fill(v, fill);
        return v;
    }

    private static SessionVectorMetadata MakeMeta(
        string signature,
        double botProbability = 0.1,
        string? clusterId = null) =>
        new()
        {
            Signature = signature,
            IsBot = botProbability >= 0.5,
            BotProbability = botProbability,
            Timestamp = DateTimeOffset.UtcNow,
            ClusterId = clusterId,
            CompressionLevel = 0
        };

    private static CompactionSignatureInfo MakePriorityInfo(
        string sig,
        double botProb = 0.5,
        string riskBand = "Medium",
        bool isBot = false,
        bool hasEntityMapping = false) =>
        new()
        {
            Signature = sig,
            SessionCount = 1,
            BotProbability = botProb,
            RiskBand = riskBand,
            IsBot = isBot,
            HasEntityMapping = hasEntityMapping,
            LastSeen = DateTime.UtcNow
        };

    // ---- Fake ISessionVectorSearch that captures ReplaceAllAsync calls ----

    private sealed class FakeVectorSearch : ISessionVectorSearch
    {
        private readonly IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> _snapshot;

        public FakeVectorSearch(
            int count,
            IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> snapshot)
        {
            Count = count;
            _snapshot = snapshot;
        }

        public int Count { get; }
        public bool ReplaceAllCalled { get; private set; }
        public IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)>? LastReplaceAllItems { get; private set; }

        public Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
        {
            ReplaceAllCalled = true;
            LastReplaceAllItems = items;
            return Task.CompletedTask;
        }

        public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot() => _snapshot;

        public Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
            float[]? velocityVector = null, float[]? frequencyFingerprint = null, float[]? driftVector = null)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(float[] vector, int topK = 10, float minSimilarity = 0.70f)
            => Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(float[] vector, int topK = 10, float maxDistance = 5.0f)
            => Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        public Task<IReadOnlyList<GhostCentroidMatch>> FindGhostCentroidsAsync(float[] vector, int topK = 5, float minSimilarity = 0.75f)
            => Task.FromResult<IReadOnlyList<GhostCentroidMatch>>(Array.Empty<GhostCentroidMatch>());

        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }

    // ---- Minimal IDetectionArchive fake: only GetSignaturePriorityInfoAsync matters ----

    private sealed class FakeStore : IDetectionArchive
    {
        private readonly List<CompactionSignatureInfo> _priorityInfos;

        public FakeStore(List<CompactionSignatureInfo> priorityInfos) =>
            _priorityInfos = priorityInfos;

        public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(
            List<string> signatures, CancellationToken ct = default) =>
            Task.FromResult(_priorityInfos.Where(p => signatures.Contains(p.Signature)).ToList());

        // ---- All other members are no-ops ----

        public string? PersistenceConnectionString => null;

        public Task<long> AddSessionAsync(RequestScope scope, PersistedSession session, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task<long> AddEchoAsync(SessionEcho echo, CancellationToken ct = default)
            => Task.FromResult(0L);

        public Task UpsertSignatureAsync(RequestScope scope, PersistedSignature signature, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task AddRequestAsync(RequestScope scope, PersistedRequest request, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task AddRequestBatchAsync(RequestScope scope, IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default)
            => Task.FromResult(new List<PersistedRequest>());

        public Task<List<PersistedRequest>> GetRecentRequestsAsync(int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default)
            => Task.FromResult(new List<PersistedRequest>());

        public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default)
            => Task.FromResult(new List<PersistedSession>());

        public Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, DateTime? since = null, CancellationToken ct = default)
            => Task.FromResult(new List<PersistedSession>());

        public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default)
            => Task.FromResult<PersistedSignature?>(null);

        public Task<string> ResolveSignatureAsync(string requestedSignatureId, CancellationToken ct = default)
            => Task.FromResult(requestedSignatureId);

        public Task RecordSignatureMergeAsync(string oldSignatureId, string newSignatureId, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default)
            => Task.FromResult(new List<PersistedSignature>());

        public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new DashboardSessionSummary());

        public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default)
            => Task.FromResult(new List<AggregatedBucket>());

        public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult(new List<CountrySessionStats>());

        public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default)
            => Task.FromResult(new List<(PersistedSession, float)>());

        public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default)
            => Task.FromResult(primarySignature);

        public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default)
            => Task.FromResult<ResolvedEntity?>(null);

        public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
            => Task.FromResult<ResolvedEntity?>(null);

        public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
            => Task.FromResult(new List<EntityEdge>());

        public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PruneAsync(TimeSpan retention, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(int maxPerSignature, int limit = 500, CancellationToken ct = default)
            => Task.FromResult(new List<(string, int)>());

        public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default)
            => Task.FromResult(new CompactionResult { Signature = signature });

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
    }
}
