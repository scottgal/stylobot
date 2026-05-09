using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Services;

public class SessionVectorWarmupServiceTests
{
    // -----------------------------------------------------------------------
    // Fake stores
    // -----------------------------------------------------------------------

    private sealed class FakeSignatureStore : ISignatureCentroidStore
    {
        private readonly IReadOnlyList<SignatureCentroidRow> _rows;
        public FakeSignatureStore(IReadOnlyList<SignatureCentroidRow> rows) => _rows = rows;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
            => Task.FromResult(_rows);

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSessionStore : ISessionCentroidStore
    {
        private readonly IReadOnlyList<SessionCentroidRow> _rows;
        public FakeSessionStore(IReadOnlyList<SessionCentroidRow> rows) => _rows = rows;

        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult(_rows);

        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeIntentStore : IIntentCentroidStore
    {
        private readonly IReadOnlyList<IntentCentroidRow> _rows;
        public FakeIntentStore(IReadOnlyList<IntentCentroidRow> rows) => _rows = rows;

        public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult(_rows);

        public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Fake search impls — capture Add/ReplaceAll calls
    // -----------------------------------------------------------------------

    private sealed class FakeSignatureSearch : ISignatureSimilaritySearch
    {
        public int AddCallCount { get; private set; }

        public Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence, string? embeddingContext = null)
        {
            AddCallCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(float[] vector, int topK = 5, float minSimilarity = 0.80f, string? embeddingContext = null)
            => Task.FromResult<IReadOnlyList<SimilarSignature>>(Array.Empty<SimilarSignature>());

        public int Count => AddCallCount;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }

    private sealed class FakeSessionSearch : ISessionVectorSearch
    {
        public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)>? ReplacedItems { get; private set; }

        public Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
        {
            ReplacedItems = items.Select(x => (x.Vector, x.Meta)).ToList();
            return Task.CompletedTask;
        }

        public int Count => ReplacedItems?.Count ?? 0;

        public Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
            float[]? velocityVector = null, float[]? frequencyFingerprint = null, float[]? driftVector = null)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(float[] vector, int topK = 10, float minSimilarity = 0.70f)
            => Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(float[] vector, int topK = 10, float maxDistance = 5.0f)
            => Task.FromResult<IReadOnlyList<SessionVectorMatch>>(Array.Empty<SessionVectorMatch>());

        public Task<IReadOnlyList<GhostCentroidMatch>> FindGhostCentroidsAsync(float[] vector, int topK = 5, float minSimilarity = 0.75f)
            => Task.FromResult<IReadOnlyList<GhostCentroidMatch>>(Array.Empty<GhostCentroidMatch>());

        public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot()
            => ReplacedItems ?? Array.Empty<(float[], SessionVectorMetadata)>();

        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }

    private sealed class FakeIntentSearch : IIntentSimilaritySearch
    {
        public int AddCallCount { get; private set; }

        public Task AddAsync(float[] vector, string signatureId, double threatScore, string intentCategory, string? reasoning = null)
        {
            AddCallCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(float[] vector, int topK = 5, float minSimilarity = 0.75f)
            => Task.FromResult<IReadOnlyList<SimilarIntent>>(Array.Empty<SimilarIntent>());

        public int Count => AddCallCount;
        public Task SaveAsync() => Task.CompletedTask;
        public Task LoadAsync() => Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Helper: build the service under test
    // -----------------------------------------------------------------------

    private static SessionVectorWarmupService Build(
        FakeSignatureStore sigStore,
        FakeSessionStore sessStore,
        FakeIntentStore intentStore,
        FakeSignatureSearch sigSearch,
        FakeSessionSearch sessSearch,
        FakeIntentSearch intentSearch,
        SelfMaintenanceOptions? opts = null)
    {
        var options = new BotDetectionOptions();
        if (opts != null) options.SelfMaintenance = opts;

        return new SessionVectorWarmupService(
            sigStore, sessStore, intentStore,
            sigSearch, sessSearch, intentSearch,
            Options.Create(options),
            NullLogger<SessionVectorWarmupService>.Instance);
    }

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartAsync_WarmsAllThreeCaches_FromSqliteCentroids()
    {
        // Arrange — 3 signature rows, 2 session rows, 1 intent row
        var sigRows = new List<SignatureCentroidRow>
        {
            new("sig-1", [0.1f, 0.2f, 0.3f], WasBot: true,  Confidence: 0.9),
            new("sig-2", [0.4f, 0.5f, 0.6f], WasBot: false, Confidence: 0.3),
            new("sig-3", [0.7f, 0.8f, 0.9f], WasBot: true,  Confidence: 0.75),
        };

        var sessRows = new List<SessionCentroidRow>
        {
            new() { SignatureId = "sess-1", Vector = [0.1f, 0.2f], IsBot = true,  BotProbability = 0.85 },
            new() { SignatureId = "sess-2", Vector = [0.3f, 0.4f], IsBot = false, BotProbability = 0.2  },
        };

        var intentRows = new List<IntentCentroidRow>
        {
            new("intent-1", [0.5f, 0.6f], ThreatScore: 0.7, IntentCategory: "scanning"),
        };

        var sigSearch    = new FakeSignatureSearch();
        var sessSearch   = new FakeSessionSearch();
        var intentSearch = new FakeIntentSearch();

        var svc = Build(
            new FakeSignatureStore(sigRows),
            new FakeSessionStore(sessRows),
            new FakeIntentStore(intentRows),
            sigSearch, sessSearch, intentSearch);

        // Act — call the three private warm helpers directly to avoid the startup delay
        // --- call the three private helpers directly ---
        var warmSig    = typeof(SessionVectorWarmupService).GetMethod("WarmSignaturesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var warmSess   = typeof(SessionVectorWarmupService).GetMethod("WarmSessionsAsync",   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var warmIntent = typeof(SessionVectorWarmupService).GetMethod("WarmIntentsAsync",     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)warmSig.Invoke(svc, [CancellationToken.None])!;
        await (Task)warmSess.Invoke(svc, [CancellationToken.None])!;
        await (Task)warmIntent.Invoke(svc, [CancellationToken.None])!;

        // Assert — signature: 3 AddAsync calls
        Assert.Equal(3, sigSearch.AddCallCount);

        // Assert — session: ReplaceAllAsync called with 2 items
        Assert.NotNull(sessSearch.ReplacedItems);
        Assert.Equal(2, sessSearch.ReplacedItems!.Count);
        Assert.Equal("sess-1", sessSearch.ReplacedItems[0].Metadata.Signature);
        Assert.Equal("sess-2", sessSearch.ReplacedItems[1].Metadata.Signature);

        // Assert — intent: 1 AddAsync call
        Assert.Equal(1, intentSearch.AddCallCount);
    }

    [Fact]
    public async Task StartAsync_EmptyStores_SkipsSilently()
    {
        // Arrange — all stores empty
        var sigSearch    = new FakeSignatureSearch();
        var sessSearch   = new FakeSessionSearch();
        var intentSearch = new FakeIntentSearch();

        var svc = Build(
            new FakeSignatureStore([]),
            new FakeSessionStore([]),
            new FakeIntentStore([]),
            sigSearch, sessSearch, intentSearch);

        var warmSig    = typeof(SessionVectorWarmupService).GetMethod("WarmSignaturesAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var warmSess   = typeof(SessionVectorWarmupService).GetMethod("WarmSessionsAsync",   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var warmIntent = typeof(SessionVectorWarmupService).GetMethod("WarmIntentsAsync",     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)warmSig.Invoke(svc, [CancellationToken.None])!;
        await (Task)warmSess.Invoke(svc, [CancellationToken.None])!;
        await (Task)warmIntent.Invoke(svc, [CancellationToken.None])!;

        // Nothing added, no exception
        Assert.Equal(0, sigSearch.AddCallCount);
        Assert.Null(sessSearch.ReplacedItems);
        Assert.Equal(0, intentSearch.AddCallCount);
    }

    [Fact]
    public async Task WarmSessions_MapsAllRowFieldsToMetadata()
    {
        // Arrange
        var velVec = new float[] { 0.1f, 0.2f };
        var varVec = new float[] { 0.01f, 0.02f };
        var freqVec = new float[] { 0.5f, 0.6f };

        var sessRows = new List<SessionCentroidRow>
        {
            new()
            {
                SignatureId      = "sess-meta",
                Vector           = [1f, 0f],
                IsBot            = true,
                BotProbability   = 0.95,
                VelocityVector   = velVec,
                VarianceVector   = varVec,
                FreqFingerprint  = freqVec,
                CompressionLevel = 1,
                Priority         = 2.5,
                ClusterId        = "cluster-42"
            }
        };

        var sessSearch = new FakeSessionSearch();
        var svc = Build(
            new FakeSignatureStore([]),
            new FakeSessionStore(sessRows),
            new FakeIntentStore([]),
            new FakeSignatureSearch(), sessSearch, new FakeIntentSearch());

        var warmSess = typeof(SessionVectorWarmupService).GetMethod("WarmSessionsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await (Task)warmSess.Invoke(svc, [CancellationToken.None])!;

        Assert.NotNull(sessSearch.ReplacedItems);
        var meta = sessSearch.ReplacedItems![0].Metadata;

        Assert.Equal("sess-meta", meta.Signature);
        Assert.True(meta.IsBot);
        Assert.Equal(0.95, meta.BotProbability);
        Assert.Equal(velVec, meta.VelocityVector);
        Assert.Equal(varVec, meta.VarianceVector);
        Assert.Equal(freqVec, meta.FrequencyFingerprint);
        Assert.Equal(1, meta.CompressionLevel);
        Assert.Equal(2.5, meta.Priority);
        Assert.Equal("cluster-42", meta.ClusterId);
        Assert.True(meta.VelocityMagnitude > 0f);
    }
}
