using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class BoundedVectorCacheTests
{
    [Fact]
    public void BoundedVectorCache_EvictsLowFrequency_WhenAtCapacity()
    {
        // Arrange: cache of size 3
        var cache = new BoundedVectorCacheTestAccessor<string>(maxSize: 3);

        // Add three entries with different access frequencies.
        // "low-freq" is added but never accessed again (frequency stays at 1).
        // "high-freq" is accessed multiple times.
        // "bot-entry" is flagged as bot (2x retention multiplier).
        cache.Set("low-freq", "data-low", isBot: false);
        cache.Set("high-freq", "data-high", isBot: false);
        cache.Set("bot-entry", "data-bot", isBot: true);

        // Bump frequency on high-freq and bot-entry
        cache.TryGet("high-freq", out _);
        cache.TryGet("high-freq", out _);
        cache.TryGet("bot-entry", out _);

        // Cache is at capacity (3); adding a 4th entry triggers eviction.
        cache.Set("new-entry", "data-new", isBot: false);

        // "low-freq" has retention score = 1 * 1.0 = 1.0 (lowest), should be evicted.
        // "high-freq" has retention score = 3 * 1.0 = 3.0.
        // "bot-entry" has retention score = 2 * 2.0 = 4.0.
        Assert.False(cache.TryGet("low-freq", out _), "low-freq should have been evicted");
        Assert.True(cache.TryGet("high-freq", out _), "high-freq should still be in cache");
        Assert.True(cache.TryGet("bot-entry", out _), "bot-entry should still be in cache (2x multiplier)");
        Assert.True(cache.TryGet("new-entry", out _), "new-entry should have been inserted");
    }
}

public class SlimSignatureSimilaritySearchTests
{
    private static SlimSignatureSimilaritySearch BuildSut(
        ISignatureCentroidStore? store = null,
        int cacheSize = 100)
    {
        var mockStore = store ?? new NoOpCentroidStore();
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = { SignatureCacheSize = cacheSize }
        });
        return new SlimSignatureSimilaritySearch(
            mockStore,
            options,
            NullLogger<SlimSignatureSimilaritySearch>.Instance);
    }

    [Fact]
    public async Task FindSimilarAsync_CacheMiss_ReturnsEmpty()
    {
        var sut = BuildSut();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.8f);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilarAsync_CacheHit_ReturnsSimilarityNearOne()
    {
        var sut = BuildSut();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        await sut.AddAsync(vector, "sig-a", wasBot: true, confidence: 0.9);

        // Small delay to let background fire, but AddAsync puts it in cache synchronously
        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.8f);

        Assert.Single(result);
        var match = result[0];
        Assert.Equal("sig-a", match.SignatureId);
        Assert.True(match.WasBot);
        Assert.Equal(0.9, match.Confidence);
        // Distance = 1 - cosine_similarity; identical vectors => cosine = 1 => distance ~ 0
        Assert.True(match.Distance < 0.01f, $"Expected near-zero distance but got {match.Distance}");
    }

    [Fact]
    public async Task FindSimilarAsync_OrthogonalVectors_ReturnsLowSimilarity()
    {
        var sut = BuildSut();
        var stored  = new float[] { 1f, 0f, 0f, 0f };
        var query   = new float[] { 0f, 1f, 0f, 0f };

        await sut.AddAsync(stored, "sig-b", wasBot: false, confidence: 0.5);

        // minSimilarity = 0.0 so orthogonal vectors still surface in results
        var result = await sut.FindSimilarAsync(query, topK: 5, minSimilarity: 0.0f);

        // Orthogonal vectors have cosine similarity = 0
        Assert.Single(result);
        var match = result[0];
        Assert.True(match.Distance > 0.99f, $"Expected distance near 1 for orthogonal vectors but got {match.Distance}");
    }

    [Fact]
    public async Task AddAsync_PopulatesCache()
    {
        var sut = BuildSut();
        Assert.Equal(0, sut.Count);

        var vector = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        await sut.AddAsync(vector, "sig-c", wasBot: false, confidence: 0.7);

        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task AddAsync_ZeroVector_IsIgnored()
    {
        var sut = BuildSut();
        var zeroVector = new float[] { 0f, 0f, 0f, 0f };

        await sut.AddAsync(zeroVector, "sig-zero", wasBot: false, confidence: 0.5);

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task SaveAsync_IsNoOp_DoesNotThrow()
    {
        var sut = BuildSut();
        // Should complete without throwing
        await sut.SaveAsync();
    }

    [Fact]
    public async Task LoadAsync_IsNoOp_DoesNotThrow()
    {
        var sut = BuildSut();
        await sut.LoadAsync();
    }

    [Fact]
    public async Task FindSimilarAsync_FrequentHit_BumpsLfuAndSurvivesEviction()
    {
        var sut = BuildSut(cacheSize: 2);
        var hotVec  = new float[] { 1f, 0f };
        var coldVec = new float[] { 0f, 1f };
        var query   = new float[] { 1f, 0f };

        await sut.AddAsync(hotVec,  "hot",  wasBot: true,  confidence: 0.9);
        await sut.AddAsync(coldVec, "cold", wasBot: false, confidence: 0.5);

        // Repeatedly match "hot" to boost its LFU frequency via Touch
        for (var i = 0; i < 5; i++)
            await sut.FindSimilarAsync(query, topK: 5, minSimilarity: 0.8f);

        // Adding a third entry triggers eviction; "cold" (lower LFU score) should be evicted
        await sut.AddAsync(new float[] { 0.5f, 0.5f }, "third", wasBot: false, confidence: 0.6);

        var results = await sut.FindSimilarAsync(query, topK: 5, minSimilarity: 0.8f);
        Assert.Contains(results, r => r.SignatureId == "hot");
    }

    [Fact]
    public async Task AddAsync_FiresBackgroundSqliteUpsert()
    {
        var capturingStore = new CapturingCentroidStore();
        var sut = BuildSut(store: capturingStore);
        var vector = new float[] { 1f, 0f };

        await sut.AddAsync(vector, "sig-d", wasBot: true, confidence: 0.85);

        // Give the background task time to complete
        await Task.Delay(200);

        Assert.Equal(1, capturingStore.UpsertCount);
        Assert.Equal("sig-d", capturingStore.LastSignatureId);
        Assert.True(capturingStore.LastWasBot);
    }

    // ------------------------------------------------------------------
    // Test helpers
    // ------------------------------------------------------------------

    private sealed class NoOpCentroidStore : ISignatureCentroidStore
    {
        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>([]);

        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingCentroidStore : ISignatureCentroidStore
    {
        public int UpsertCount { get; private set; }
        public string? LastSignatureId { get; private set; }
        public bool LastWasBot { get; private set; }

        public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence,
            CancellationToken ct = default)
        {
            UpsertCount++;
            LastSignatureId = signatureId;
            LastWasBot = wasBot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>([]);

        public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

/// <summary>
///     Thin wrapper that exposes <see cref="BoundedVectorCache{TValue}"/> internals for white-box testing.
/// </summary>
internal sealed class BoundedVectorCacheTestAccessor<TValue>
{
    private readonly BoundedVectorCache<TValue> _inner;

    public BoundedVectorCacheTestAccessor(int maxSize) =>
        _inner = new BoundedVectorCache<TValue>(maxSize);

    public bool TryGet(string key, out TValue? value) => _inner.TryGet(key, out value);
    public void Set(string key, TValue value, bool isBot = false) => _inner.Set(key, value, isBot);
    public int Count => _inner.Count;
}
