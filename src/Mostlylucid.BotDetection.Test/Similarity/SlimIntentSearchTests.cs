using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Centroids;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Similarity;

public class SlimIntentSearchTests
{
    private static SlimIntentSearch BuildSut(int cacheSize = 100)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            SelfMaintenance = { IntentCacheSize = cacheSize }
        });
        return new SlimIntentSearch(
            options,
            new NullCentroidWriter(),
            Options.Create(new CentroidWriterOptions()),
            NullLogger<SlimIntentSearch>.Instance);
    }

    [Fact]
    public async Task FindSimilar_CacheMiss_ReturnsEmpty()
    {
        var sut = BuildSut();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.75f);

        Assert.Empty(result);
    }

    [Fact]
    public async Task FindSimilar_CacheHit_IdenticalVectors_ReturnsHighSimilarity()
    {
        var sut = BuildSut();
        var vector = new float[] { 1f, 0f, 0f, 0f };

        await sut.AddAsync(vector, "intent-a", threatScore: 0.8, intentCategory: "scanning");

        var result = await sut.FindSimilarAsync(vector, topK: 5, minSimilarity: 0.75f);

        Assert.Single(result);
        var match = result[0];
        Assert.Equal("intent-a", match.SignatureId);
        Assert.Equal(0.8, match.ThreatScore);
        Assert.Equal("scanning", match.IntentCategory);
        // Distance = 1 - cosine_similarity; identical vectors => cosine = 1 => distance ~ 0
        Assert.True(match.Distance < 0.01f, $"Expected near-zero distance but got {match.Distance}");
    }

    [Fact]
    public async Task AddIntent_PopulatesCache()
    {
        var sut = BuildSut();
        Assert.Equal(0, sut.Count);

        var vector = new float[] { 0.5f, 0.5f, 0.5f, 0.5f };
        await sut.AddAsync(vector, "intent-b", threatScore: 0.6, intentCategory: "browsing");

        Assert.Equal(1, sut.Count);
    }

    [Fact]
    public async Task SaveAsync_IsNoOp_DoesNotThrow()
    {
        var sut = BuildSut();
        // Should complete without throwing
        await sut.SaveAsync();
    }

    [Fact]
    public async Task ReplaceAllAsync_ClearsAndRepopulates()
    {
        var sut = BuildSut();

        // Add initial entry
        var oldVector = new float[] { 1f, 0f, 0f, 0f };
        await sut.AddAsync(oldVector, "old-intent", threatScore: 0.5, intentCategory: "browsing");
        Assert.Equal(1, sut.Count);

        // Replace with a different entry
        var newVector = new float[] { 0f, 1f, 0f, 0f };
        await sut.ReplaceAllAsync([
            ("new-intent", newVector, 0.7, "scanning")
        ]);

        // Only the new entry should be present
        Assert.Equal(1, sut.Count);

        // Old entry should no longer match
        var oldQuery = await sut.FindSimilarAsync(oldVector, topK: 5, minSimilarity: 0.75f);
        Assert.Empty(oldQuery);

        // New entry should match
        var newQuery = await sut.FindSimilarAsync(newVector, topK: 5, minSimilarity: 0.75f);
        Assert.Single(newQuery);
        Assert.Equal("new-intent", newQuery[0].SignatureId);
    }

}
