using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
///     Tests for trajectory modeling: drift vector computation, forward projection,
///     and Mahalanobis distance.
/// </summary>
public class SessionVectorDriftTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 4, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeDriftVector_WithFewSessions_ReturnsZeroVector()
    {
        var sessions = new List<SessionSnapshot>
        {
            MakeSnapshot(BaseTime, [0.5f, 0.5f, 0f]),
            MakeSnapshot(BaseTime.AddHours(1), [0.6f, 0.4f, 0f])
        };

        var drift = SessionVectorizer.ComputeDriftVector(sessions);

        Assert.Equal(SessionVectorizer.Dimensions, drift.Length);
        Assert.All(drift, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void ComputeDriftVector_WithLinearTrend_ReturnsPositiveSlope()
    {
        // Create sessions with a clear linear trend in dim 0: 0.1 → 0.4 → 0.7
        // (values are then normalized per vector; we test the slope direction)
        var sessions = new List<SessionSnapshot>();
        for (var i = 0; i < 5; i++)
        {
            var v = new float[SessionVectorizer.Dimensions];
            v[0] = 0.1f + i * 0.1f; // Clear increasing trend
            v[1] = 0.5f;             // Constant
            sessions.Add(MakeSnapshot(BaseTime.AddHours(i), v));
        }

        var drift = SessionVectorizer.ComputeDriftVector(sessions);

        // Dim 0 should have positive slope; dim 1 should have near-zero slope
        Assert.True(drift[0] > 0, $"Expected positive slope in dim 0, got {drift[0]}");
        Assert.True(Math.Abs(drift[1]) < Math.Abs(drift[0]) * 5,
            $"Dim 1 slope ({drift[1]:F4}) should be smaller than dim 0 ({drift[0]:F4})");
    }

    [Fact]
    public void ProjectForward_WithPositiveDrift_IncreasesValues()
    {
        var current = new float[SessionVectorizer.Dimensions];
        current[0] = 0.3f;
        current[1] = 0.7f;
        // Normalize
        var norm = MathF.Sqrt(current.Sum(v => v * v));
        for (var i = 0; i < current.Length; i++) current[i] /= norm;

        var drift = new float[SessionVectorizer.Dimensions];
        drift[0] = 0.2f; // Push dim 0 up

        var predicted = SessionVectorizer.ProjectForward(current, drift, 1.0f);

        // Predicted should be L2-normalized
        var predNorm = MathF.Sqrt(predicted.Sum(v => v * v));
        Assert.True(Math.Abs(predNorm - 1.0f) < 0.01f, $"Predicted vector should be normalized, norm={predNorm:F4}");

        // Before normalization, dim 0 should have increased
        // After normalization, the ratio dim0/dim1 should be higher in predicted
        Assert.True(predicted[0] / predicted[1] > current[0] / current[1],
            "Drift should push predicted position toward higher dim 0");
    }

    [Fact]
    public void MahalanobisDistance_HighVarianceDimension_LowerPenalty()
    {
        // Two vectors that differ in dim 0
        var query = new float[] { 1.0f, 0.5f };
        var centroid = new float[] { 0.0f, 0.5f }; // Differs in dim 0 by 1.0

        // Low variance in dim 0: that difference is anomalous
        var lowVariance = new float[] { 0.01f, 0.5f };
        // High variance in dim 0: that difference is expected noise
        var highVariance = new float[] { 1.0f, 0.5f };

        var distLow = SessionVectorizer.MahalanobisDistance(query, centroid, lowVariance);
        var distHigh = SessionVectorizer.MahalanobisDistance(query, centroid, highVariance);

        Assert.True(distLow > distHigh,
            $"Low variance should produce higher Mahalanobis distance ({distLow:F3} > {distHigh:F3})");
    }

    [Fact]
    public void MahalanobisDistance_SameVector_IsZero()
    {
        var v = new float[] { 0.3f, 0.7f, 0.0f };
        var variance = new float[] { 0.1f, 0.1f, 0.1f };

        var dist = SessionVectorizer.MahalanobisDistance(v, v, variance);

        Assert.Equal(0f, dist, 6);
    }

    [Fact]
    public void ComputeVarianceVector_IdenticalVectors_ReturnsZeroVariance()
    {
        var v = new float[] { 0.5f, 0.3f, 0.2f };
        var vectors = new List<float[]> { v, v, v };

        var variance = SessionVectorizer.ComputeVarianceVector(vectors);

        Assert.NotNull(variance);
        Assert.All(variance!, vv => Assert.Equal(0f, vv, 5));
    }

    [Fact]
    public void ComputeVarianceVector_WithSpread_ReturnsNonZeroVariance()
    {
        var vectors = new List<float[]>
        {
            new float[] { 0.0f, 0.5f },
            new float[] { 1.0f, 0.5f },
            new float[] { 0.5f, 0.5f }
        };

        var variance = SessionVectorizer.ComputeVarianceVector(vectors);

        Assert.NotNull(variance);
        Assert.True(variance![0] > 0, "Dim 0 has spread; variance should be positive");
        Assert.Equal(0f, variance[1], 5); // Dim 1 is constant
    }

    private static SessionSnapshot MakeSnapshot(DateTimeOffset time, float[]? vector = null)
    {
        var v = vector ?? new float[SessionVectorizer.Dimensions];
        if (v.Length < SessionVectorizer.Dimensions)
        {
            var full = new float[SessionVectorizer.Dimensions];
            Array.Copy(v, full, v.Length);
            v = full;
        }

        return new SessionSnapshot
        {
            Signature = "test",
            StartedAt = time.AddMinutes(-5),
            EndedAt = time,
            RequestCount = 10,
            Vector = v,
            Maturity = 0.8f,
            DominantState = RequestState.PageView
        };
    }
}
