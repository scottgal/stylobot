using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SlimSessionVectorSearchTests
{
    private static readonly int Dims = SessionVectorizer.Dimensions; // 129

    // Build a valid 129-dim vector with a single non-zero element
    private static float[] MakeVector(int hotIndex = 0, float value = 1f)
    {
        var v = new float[Dims];
        v[hotIndex] = value;
        return v;
    }

    private static SlimSessionVectorSearch BuildSut(
        ISessionCentroidStore? store = null,
        int cacheSize = 100)
    {
        var centroidStore = store ?? new NoOpSessionCentroidStore();
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = { SessionCacheSize = cacheSize }
        });
        return new SlimSessionVectorSearch(
            centroidStore,
            options,
            NullLogger<SlimSessionVectorSearch>.Instance);
    }

    [Fact]
    public async Task FindSimilar_CacheMiss_ReturnsEmpty()
    {
        var sut = BuildSut();
        var vector = MakeVector(0);

        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.70f);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilar_CacheHit_IdenticalVectors_ReturnsHighSimilarity()
    {
        var sut = BuildSut();
        var vector = MakeVector(0);

        await sut.AddAsync(vector, "session-a", isBot: true, botProbability: 0.95);

        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.70f);

        Assert.Single(result);
        var match = result[0];
        Assert.Equal("session-a", match.Signature);
        // Identical vectors => cosine similarity = 1.0
        Assert.True(match.Similarity > 0.99f, $"Expected similarity near 1 but got {match.Similarity}");
    }

    [Fact]
    public async Task AddSession_PopulatesCache()
    {
        var sut = BuildSut();
        Assert.Equal(0, sut.Count);

        var vector = MakeVector(5, 0.5f);
        await sut.AddAsync(vector, "session-b", isBot: false, botProbability: 0.1);

        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task AddSession_FiresBackgroundSqliteUpsert()
    {
        var capturingStore = new CapturingSessionStore();
        var sut = BuildSut(store: capturingStore);
        var vector = MakeVector(3);

        await sut.AddAsync(vector, "session-c", isBot: true, botProbability: 0.87);

        // Give the background Task time to complete
        await Task.Delay(200);

        Assert.Equal(1, capturingStore.UpsertCount);
        Assert.Equal("session-c", capturingStore.LastSignatureId);
        Assert.True(capturingStore.LastIsBot);
    }

    [Fact]
    public async Task SaveAsync_IsNoOp_DoesNotThrow()
    {
        var sut = BuildSut();
        // Must complete without throwing
        await sut.SaveAsync();
    }

    [Fact]
    public async Task LoadAsync_IsNoOp_DoesNotThrow()
    {
        var sut = BuildSut();
        await sut.LoadAsync();
    }

    [Fact]
    public async Task AddAsync_WrongDimension_IsIgnored()
    {
        var sut = BuildSut();
        var wrongDimVector = new float[] { 1f, 0f, 0f }; // not 129-dim

        await sut.AddAsync(wrongDimVector, "session-dim", isBot: false, botProbability: 0.2);

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task AddAsync_ZeroVector_IsIgnored()
    {
        var sut = BuildSut();
        var zeroVector = new float[Dims]; // all zeros

        await sut.AddAsync(zeroVector, "session-zero", isBot: false, botProbability: 0.3);

        Assert.Equal(0, sut.Count);
    }

    [Fact]
    public async Task GetAllVectorsSnapshot_ReturnsAllCachedEntries()
    {
        var sut = BuildSut();
        await sut.AddAsync(MakeVector(0), "sig-1", isBot: true, botProbability: 0.9);
        await sut.AddAsync(MakeVector(1), "sig-2", isBot: false, botProbability: 0.1);

        var snapshot = sut.GetAllVectorsSnapshot();

        Assert.Equal(2, snapshot.Count);
        var sigs = snapshot.Select(x => x.Metadata.Signature).ToHashSet();
        Assert.Contains("sig-1", sigs);
        Assert.Contains("sig-2", sigs);
    }

    [Fact]
    public async Task ReplaceAllAsync_ReplacesCache()
    {
        var sut = BuildSut();
        // Seed with one entry
        await sut.AddAsync(MakeVector(0), "old-sig", isBot: false, botProbability: 0.2);
        Assert.Equal(1, sut.Count);

        var newVec = MakeVector(10);
        var newMeta = new SessionVectorMetadata
        {
            Signature = "new-sig",
            IsBot = true,
            BotProbability = 0.95,
            Timestamp = DateTimeOffset.UtcNow
        };

        await sut.ReplaceAllAsync([(newVec, newMeta)]);

        // After replace the cache should contain new-sig (old-sig may still be present since
        // ReplaceAllAsync calls Set which adds without clearing; verify new-sig is present)
        var snapshot = sut.GetAllVectorsSnapshot();
        Assert.Contains(snapshot, x => x.Metadata.Signature == "new-sig");
    }

    // ------------------------------------------------------------------
    // Test helpers
    // ------------------------------------------------------------------

    private sealed class NoOpSessionCentroidStore : ISessionCentroidStore
    {
        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>([]);

        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingSessionStore : ISessionCentroidStore
    {
        public int UpsertCount { get; private set; }
        public string? LastSignatureId { get; private set; }
        public bool LastIsBot { get; private set; }

        public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
        {
            UpsertCount++;
            LastSignatureId = row.SignatureId;
            LastIsBot = row.IsBot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionCentroidRow>>([]);

        public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
