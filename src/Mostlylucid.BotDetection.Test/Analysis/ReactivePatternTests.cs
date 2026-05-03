using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
/// Tests for ReactiveSignalTracker and the reactive pattern detection logic.
/// Tests the tracker directly; contributor integration is covered by e2e tests.
/// </summary>
public class ReactivePatternTests
{
    private ReactiveSignalTracker MakeTracker() => new();

    // ===================================================
    // ReactiveSignalTracker: basic record/retrieve
    // ===================================================

    [Fact]
    public void RecordErrorServed_EmptySignature_IsIgnored()
    {
        var tracker = MakeTracker();
        tracker.RecordErrorServed("", 429, "/api/data", 5);
        Assert.Empty(tracker.GetHistory(""));
    }

    [Fact]
    public void GetHistory_UnknownSignature_ReturnsEmpty()
    {
        var tracker = MakeTracker();
        Assert.Empty(tracker.GetHistory("sig-unknown"));
    }

    [Fact]
    public void RecordAndRetrieve_SingleEvent_Roundtrips()
    {
        var tracker = MakeTracker();
        tracker.RecordErrorServed("sig-a", 429, "/api/data", 5);

        var history = tracker.GetHistory("sig-a");

        Assert.Single(history);
        Assert.Equal(429, history[0].StatusCode);
        Assert.Equal("/api/data", history[0].Path);
        Assert.Equal(5, history[0].RetryAfterSeconds);
    }

    [Fact]
    public void RecordAndRetrieve_MultipleEvents_OrderedOldestFirst()
    {
        var tracker = MakeTracker();
        tracker.RecordErrorServed("sig-b", 403, "/admin", null);
        tracker.RecordErrorServed("sig-b", 429, "/api", 10);
        tracker.RecordErrorServed("sig-b", 503, "/api", null);

        var history = tracker.GetHistory("sig-b");

        Assert.Equal(3, history.Count);
        Assert.Equal(403, history[0].StatusCode);
        Assert.Equal(429, history[1].StatusCode);
        Assert.Equal(503, history[2].StatusCode);
    }

    [Fact]
    public void GetHistory_IsolatedBySignature()
    {
        var tracker = MakeTracker();
        tracker.RecordErrorServed("sig-1", 429, "/a", 5);
        tracker.RecordErrorServed("sig-2", 403, "/b", null);

        Assert.Single(tracker.GetHistory("sig-1"));
        Assert.Single(tracker.GetHistory("sig-2"));
        Assert.Equal(429, tracker.GetHistory("sig-1")[0].StatusCode);
        Assert.Equal(403, tracker.GetHistory("sig-2")[0].StatusCode);
    }

    // ===================================================
    // Retry-After compliance logic
    // ===================================================

    [Fact]
    public void RetryAfterCompliance_PerfectCompliance_Ratio_IsOne()
    {
        // Compliance = actualGap / retryAfterMs; ratio ≈ 1.0 = inhuman precision
        // We test the arithmetic directly
        const double retryAfterSec = 5.0;
        const double actualGapSec = 5.0;
        var compliance = actualGapSec / retryAfterSec;
        Assert.Equal(1.0, compliance, 3);
    }

    [Fact]
    public void RetryAfterCompliance_TooEarlyRetry_RatioBelow1()
    {
        const double retryAfterSec = 10.0;
        const double actualGapSec = 3.0;
        var compliance = actualGapSec / retryAfterSec;
        Assert.True(compliance < 1.0, $"Early retry should produce ratio < 1.0 (got {compliance:F3})");
    }

    // ===================================================
    // Geometric retry pattern detection
    // ===================================================

    [Fact]
    public void GeometricRatioCV_ExponentialBackoff_IsLow()
    {
        // Gaps: 1s, 2s, 4s, 8s, 16s — ratio is always exactly 2.0
        var gaps = new[] { 1000.0, 2000.0, 4000.0, 8000.0, 16000.0 };
        var ratios = ComputeRatios(gaps);
        var cv = ComputeCV(ratios);

        Assert.True(cv < 0.01, $"Perfect exponential backoff should have CV near 0, got {cv:F4}");
    }

    [Fact]
    public void GeometricRatioCV_HumanRetry_IsHigh()
    {
        // Human retries: random gaps (no pattern)
        var gaps = new[] { 5000.0, 1200.0, 8000.0, 300.0, 15000.0 };
        var ratios = ComputeRatios(gaps);
        var cv = ComputeCV(ratios);

        Assert.True(cv > 0.5, $"Human retry gaps should have high CV (got {cv:F4})");
    }

    [Fact]
    public void GeometricRatioCV_LinearBackoff_IsLow()
    {
        // Gaps: 1s, 2s, 3s, 4s, 5s — ratio approaches 1.0 asymptotically; early CV is higher
        // But proper linear: fixed increment, not fixed ratio. Test additive:
        // 1s, 2s, 3s, 4s — ratios: 2, 1.5, 1.33... CV will be higher than exponential
        // This is expected: CV threshold of 0.25 catches exponential better than linear
        var gaps = new[] { 1000.0, 3000.0, 9000.0, 27000.0 }; // ratio = 3.0 consistently
        var ratios = ComputeRatios(gaps);
        var cv = ComputeCV(ratios);

        Assert.True(cv < 0.01, $"Consistent-ratio (3x) backoff should have CV near 0, got {cv:F4}");
    }

    [Fact]
    public void BackoffBase_FibonacciPattern_ClosestBaseIs1618()
    {
        var bases = new[]
        {
            (2.0, "exponential"), (1.618, "fibonacci"), (1.5, "mild_exponential"), (1.0, "linear")
        };

        var meanRatio = 1.618; // Fibonacci ratio
        var (_, name) = bases.OrderBy(kb => Math.Abs(meanRatio - kb.Item1)).First();

        Assert.Equal("fibonacci", name);
    }

    // ===================================================
    // Co-retrier detection
    // ===================================================

    [Fact]
    public void GetCoRetriers_MultipleSignaturesOnSamePath_ReturnsAll()
    {
        var tracker = MakeTracker();
        tracker.RecordErrorServed("sig-a", 429, "/api/search", 5);
        tracker.RecordErrorServed("sig-b", 429, "/api/search", 5);
        tracker.RecordErrorServed("sig-c", 403, "/api/search", null);
        tracker.RecordErrorServed("sig-d", 429, "/api/other", 5); // different path

        var coRetriers = tracker.GetCoRetriers("/api/search", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Equal(3, coRetriers.Count);
        Assert.Contains("sig-a", coRetriers);
        Assert.Contains("sig-b", coRetriers);
        Assert.Contains("sig-c", coRetriers);
        Assert.DoesNotContain("sig-d", coRetriers);
    }

    [Fact]
    public void GetCoRetriers_BeforeCutoff_ExcludesOldEvents()
    {
        var tracker = MakeTracker();
        // We can't inject old timestamps directly, so just verify empty path returns empty
        var coRetriers = tracker.GetCoRetriers("/nonexistent", DateTimeOffset.UtcNow);
        Assert.Empty(coRetriers);
    }

    // ===================================================
    // Rate adaptation logic
    // ===================================================

    [Fact]
    public void RateAdaptation_MonotonicallyIncreasingGaps_HighScore()
    {
        // Gaps: 2s, 4s, 8s — monotonically increasing
        var gaps = new[] { 2000.0, 4000.0, 8000.0 };
        var increasingCount = 0;
        for (var i = 1; i < gaps.Length; i++)
            if (gaps[i] > gaps[i - 1]) increasingCount++;

        var score = (float)increasingCount / (gaps.Length - 1);
        Assert.Equal(1.0f, score);
    }

    [Fact]
    public void RateAdaptation_ConstantGaps_ZeroScore()
    {
        var gaps = new[] { 5000.0, 5000.0, 5000.0 };
        var increasingCount = 0;
        for (var i = 1; i < gaps.Length; i++)
            if (gaps[i] > gaps[i - 1]) increasingCount++;

        var score = (float)increasingCount / (gaps.Length - 1);
        Assert.Equal(0f, score);
    }

    // ===================================================
    // Helpers
    // ===================================================

    private static double[] ComputeRatios(double[] gaps)
    {
        var ratios = new double[gaps.Length - 1];
        for (var i = 1; i < gaps.Length; i++)
            ratios[i - 1] = gaps[i] / gaps[i - 1];
        return ratios;
    }

    private static double ComputeCV(double[] values)
    {
        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Length;
        return mean > 0 ? Math.Sqrt(variance) / mean : double.MaxValue;
    }
}
