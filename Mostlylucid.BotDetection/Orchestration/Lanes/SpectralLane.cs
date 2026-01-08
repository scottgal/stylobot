using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Spectral analysis lane - analyzes frequency patterns, periodicity, FFT of request timing.
/// </summary>
internal sealed class SpectralLane : AnalysisLaneBase
{
    public SpectralLane(SignalSink sink) : base(sink)
    {
    }

    public override string Name => "spectral";

    public override Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window,
        CancellationToken cancellationToken = default)
    {
        if (window.Count == 0)
        {
            EmitScore(0.0);
            return Task.CompletedTask;
        }

        // Compute spectral indicators
        var periodicityScore = DetectPeriodicity(window);
        var regularityScore = ComputeTimingRegularity(window);
        var burstScore = DetectBurstPatterns(window);
        var dominantFreqScore = ComputeDominantFrequencyScore(window);

        // Weighted combination (higher = more bot-like)
        var score = periodicityScore * 0.3 +
                    regularityScore * 0.3 +
                    burstScore * 0.2 +
                    dominantFreqScore * 0.2;

        // Emit component signals for observability
        Sink.Raise("spectral.periodicity", periodicityScore.ToString("F4"));
        Sink.Raise("spectral.regularity", regularityScore.ToString("F4"));
        Sink.Raise("spectral.burst", burstScore.ToString("F4"));
        Sink.Raise("spectral.dominant_freq", dominantFreqScore.ToString("F4"));

        EmitScore(Math.Clamp(score, 0.0, 1.0));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Detects periodic patterns using autocorrelation-like analysis.
    ///     Strong periodicity indicates bot-like timing.
    /// </summary>
    private static double DetectPeriodicity(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 4) return 0.0;

        var intervals = GetIntervals(window);
        if (intervals.Count < 3) return 0.0;

        // Check for repeating interval patterns
        var mean = intervals.Average();
        var deviations = intervals.Select(i => Math.Abs(i - mean) / mean).ToList();

        // Low mean deviation = high periodicity (bot-like)
        var avgDeviation = deviations.Average();
        return Math.Clamp(1 - avgDeviation, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes timing regularity by analyzing interval distribution.
    ///     Tight clustering around specific intervals indicates bots.
    /// </summary>
    private static double ComputeTimingRegularity(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 3) return 0.0;

        var intervals = GetIntervals(window);
        if (intervals.Count < 2) return 0.0;

        // Quantize intervals into buckets (100ms resolution)
        var buckets = intervals
            .Select(i => (int)(i / 100) * 100)
            .GroupBy(b => b)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (buckets.Count == 0) return 0.0;

        // High concentration in few buckets = bot-like
        var topBucketRatio = buckets[0].Count() / (double)intervals.Count;
        return Math.Clamp(topBucketRatio, 0.0, 1.0);
    }

    /// <summary>
    ///     Detects burst patterns - clusters of rapid requests followed by pauses.
    ///     Common bot pattern for rate-limited scraping.
    /// </summary>
    private static double DetectBurstPatterns(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 5) return 0.0;

        var intervals = GetIntervals(window);
        if (intervals.Count < 4) return 0.0;

        var fastThreshold = 200; // ms - considered "fast"
        var slowThreshold = 2000; // ms - considered "slow"

        var transitions = 0;
        var lastWasFast = intervals[0] < fastThreshold;

        for (var i = 1; i < intervals.Count; i++)
        {
            var isFast = intervals[i] < fastThreshold;
            var isSlow = intervals[i] > slowThreshold;

            // Count fast->slow transitions (burst patterns)
            if (lastWasFast && isSlow) transitions++;

            lastWasFast = isFast;
        }

        // Normalize by number of intervals
        return Math.Clamp(transitions / (double)(intervals.Count / 2), 0.0, 1.0);
    }

    /// <summary>
    ///     Computes dominant frequency score using simplified DFT analysis.
    ///     Strong dominant frequency indicates automated timing.
    /// </summary>
    private static double ComputeDominantFrequencyScore(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 8) return 0.0;

        var intervals = GetIntervals(window);
        if (intervals.Count < 7) return 0.0;

        // Simplified frequency analysis: find if there's a dominant period
        // by checking how many intervals cluster around the median
        var sorted = intervals.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];

        var nearMedian = intervals.Count(i => Math.Abs(i - median) < median * 0.2);
        var ratio = nearMedian / (double)intervals.Count;

        // High ratio = strong dominant frequency = bot-like
        return Math.Clamp(ratio, 0.0, 1.0);
    }

    private static List<double> GetIntervals(IReadOnlyList<OperationCompleteSignal> window)
    {
        var intervals = new List<double>();
        for (var i = 1; i < window.Count; i++)
        {
            var interval = (window[i].Timestamp - window[i - 1].Timestamp).TotalMilliseconds;
            if (interval > 0) intervals.Add(interval);
        }
        return intervals;
    }
}