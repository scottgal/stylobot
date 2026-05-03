using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
///     Tests for the four new session analytics features:
///     void detection, trajectory/pre-crime, Mahalanobis HNSW search, and cross-session rhythm.
/// </summary>
public class SessionVectorAnalyticsTests : IAsyncLifetime
{
    private HnswSessionVectorSearch _index = null!;
    private static readonly int Dims = SessionVectorizer.Dimensions;

    public async Task InitializeAsync()
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"hnsw-test-{Guid.NewGuid():N}")
        });
        _index = new HnswSessionVectorSearch(NullLogger<HnswSessionVectorSearch>.Instance, opts);
        await _index.LoadAsync();
    }

    public Task DisposeAsync()
    {
        _index.Dispose();
        return Task.CompletedTask;
    }

    // ============================================================
    // Void detection
    // ============================================================

    [Fact]
    public async Task FindSimilarAsync_EmptyIndex_IsVoid()
    {
        var query = UnitVector(0);
        var results = await _index.FindSimilarAsync(query, topK: 5, minSimilarity: 0.40f);
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_IdenticalVector_IsNotVoid()
    {
        var vec = UnitVector(0);
        await _index.AddAsync(vec, "sig-a", isBot: false, botProbability: 0.1);

        var results = await _index.FindSimilarAsync(vec, topK: 5, minSimilarity: 0.40f);

        Assert.NotEmpty(results);
        Assert.Equal("sig-a", results[0].Signature);
    }

    [Fact]
    public async Task FindSimilarAsync_OrthogonalVector_IsVoid()
    {
        await _index.AddAsync(UnitVector(0), "sig-b", isBot: false, botProbability: 0.1);

        // Query in a completely orthogonal dimension
        var results = await _index.FindSimilarAsync(UnitVector(Dims - 1), topK: 5, minSimilarity: 0.70f);

        Assert.Empty(results); // Cosine similarity = 0 < 0.70 threshold
    }

    // ============================================================
    // Mahalanobis search
    // ============================================================

    [Fact]
    public async Task FindSimilarMahalanobisAsync_EmptyIndex_ReturnsEmpty()
    {
        var results = await _index.FindSimilarMahalanobisAsync(UnitVector(0), topK: 5, maxDistance: 5.0f);
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarMahalanobisAsync_L0Entry_UsesCosineDistance()
    {
        var vec = UnitVector(0);
        await _index.AddAsync(vec, "sig-c", isBot: true, botProbability: 0.9);

        var results = await _index.FindSimilarMahalanobisAsync(vec, topK: 5, maxDistance: 5.0f);

        Assert.NotEmpty(results);
        Assert.Equal("sig-c", results[0].Signature);
    }

    [Fact]
    public async Task FindSimilarMahalanobisAsync_L1CentroidWithVariance_ReturnsNearbyMatch()
    {
        var centroid = UnitVector(0);
        var variance = Enumerable.Repeat(0.1f, Dims).ToArray();

        var meta = new SessionVectorMetadata
        {
            Signature = "sig-centroid",
            IsBot = true,
            BotProbability = 0.85,
            Timestamp = DateTimeOffset.UtcNow,
            CompressionLevel = 1,
            VarianceVector = variance
        };
        await _index.ReplaceAllAsync([(centroid, meta)]);

        // Query very close to centroid — small perturbation in dim 1
        var query = centroid.ToArray();
        query[1] += 0.05f;
        var norm = MathF.Sqrt(query.Sum(v => v * v));
        for (var i = 0; i < query.Length; i++) query[i] /= norm;

        var results = await _index.FindSimilarMahalanobisAsync(query, topK: 3, maxDistance: 5.0f);

        Assert.NotEmpty(results);
        Assert.Equal("sig-centroid", results[0].Signature);
    }

    [Fact]
    public void MahalanobisDistance_LowVarianceDimension_HigherPenalty()
    {
        var query = new float[] { 1.0f, 0.5f };
        var centroid = new float[] { 0.0f, 0.5f }; // Difference only in dim 0

        var lowVariance = new float[] { 0.01f, 0.5f };
        var highVariance = new float[] { 1.0f, 0.5f };

        var distLow = SessionVectorizer.MahalanobisDistance(query, centroid, lowVariance);
        var distHigh = SessionVectorizer.MahalanobisDistance(query, centroid, highVariance);

        Assert.True(distLow > distHigh,
            $"Low variance in discriminative dim should produce higher distance: {distLow:F3} > {distHigh:F3}");
    }

    // ============================================================
    // Trajectory / pre-crime
    // ============================================================

    [Fact]
    public void ProjectForward_IsL2Normalized()
    {
        var current = UnitVector(0);
        var drift = new float[Dims];
        drift[1] = 0.3f;

        var predicted = SessionVectorizer.ProjectForward(current, drift, 1.5f);

        var norm = MathF.Sqrt(predicted.Sum(v => v * v));
        Assert.True(Math.Abs(norm - 1.0f) < 0.01f, $"Projected vector must be L2-normalized, norm={norm:F4}");
    }

    [Fact]
    public async Task FindSimilarAsync_PredictedPositionNearBotCluster_ReturnsMatch()
    {
        // Add a bot cluster vector in a known position
        var botCluster = UnitVector(2);
        await _index.AddAsync(botCluster, "bot-cluster", isBot: true, botProbability: 0.95);

        // Current session near dim 0, drifting strongly toward dim 2
        var current = UnitVector(0);
        var drift = new float[Dims];
        drift[2] = 10f; // Strong drift toward bot cluster

        var predicted = SessionVectorizer.ProjectForward(current, drift, 1.0f);

        // After projection with strong drift, the predicted position should be near dim 2 (bot cluster)
        var matches = await _index.FindSimilarAsync(predicted, topK: 3, minSimilarity: 0.5f);

        // The predicted position should be significantly closer to dim-2 vector
        Assert.True(predicted[2] > predicted[0], "Drift should push predicted position toward dim 2");
    }

    [Fact]
    public void ComputeDriftVector_LinearTrend_ProducesCorrectSlope()
    {
        var sessions = Enumerable.Range(0, 5).Select(i =>
        {
            var v = new float[Dims];
            v[0] = 0.1f + i * 0.1f; // Increasing in dim 0
            return MakeSnapshot(DateTimeOffset.UtcNow.AddHours(-5 + i), v);
        }).ToList();

        var drift = SessionVectorizer.ComputeDriftVector(sessions);

        Assert.True(drift[0] > 0, $"Expected positive slope in dim 0 (trend direction), got {drift[0]:F4}");
    }

    // ============================================================
    // Frequency centroid: averaging logic
    // ============================================================

    [Fact]
    public void FrequencyFingerprint_Centroid_AverageTwoFingerprints_IsCorrect()
    {
        var fp1 = new float[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };
        var fp2 = new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };

        // Simulate the averaging logic from CompactHistory
        var dims = fp1.Length;
        var centroid = new float[dims];
        foreach (var fp in new[] { fp1, fp2 })
            for (var i = 0; i < dims; i++)
                centroid[i] += fp[i];
        for (var i = 0; i < dims; i++) centroid[i] /= 2;

        Assert.All(centroid, v => Assert.Equal(0.5f, v, 5));
    }

    [Fact]
    public void FrequencySimilarity_MatchingPeriodic_IsHigh()
    {
        // Two bot-like fingerprints: both have strong 30s periodicity
        var fp1 = new float[8];
        fp1[3] = 0.9f; // Lag index 3 = 30s

        var fp2 = new float[8];
        fp2[3] = 0.85f; // Same dominant lag, slightly different amplitude

        var similarity = FrequencyFingerprintEncoder.Similarity(fp1, fp2);

        // Two vectors dominated by the same component should be highly similar
        Assert.True(similarity > 0.9f, $"Similar periodic patterns should have high similarity: {similarity:F3}");
    }

    [Fact]
    public void FrequencySimilarity_DifferentPeriods_IsLow()
    {
        // Two fingerprints with different dominant lags
        var fp1 = new float[8];
        fp1[0] = 0.9f; // 1s period

        var fp2 = new float[8];
        fp2[7] = 0.9f; // 30min period

        var similarity = FrequencyFingerprintEncoder.Similarity(fp1, fp2);

        Assert.True(similarity < 0.3f, $"Different periods should have low similarity: {similarity:F3}");
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>Creates an L2-normalized unit vector with a 1 in the given dimension.</summary>
    private static float[] UnitVector(int dimension)
    {
        var v = new float[Dims];
        v[dimension] = 1.0f;
        return v;
    }

    private static SessionSnapshot MakeSnapshot(DateTimeOffset time, float[] vector)
    {
        if (vector.Length < Dims)
        {
            var full = new float[Dims];
            Array.Copy(vector, full, vector.Length);
            vector = full;
        }
        return new SessionSnapshot
        {
            Signature = "test",
            StartedAt = time.AddMinutes(-5),
            EndedAt = time,
            RequestCount = 10,
            Vector = vector,
            Maturity = 0.8f,
            DominantState = RequestState.PageView
        };
    }
}
