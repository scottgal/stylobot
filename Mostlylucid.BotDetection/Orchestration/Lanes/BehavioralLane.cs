using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Behavioral analysis lane - analyzes timing patterns, path entropy, request sequences.
/// </summary>
internal sealed class BehavioralLane : AnalysisLaneBase
{
    public BehavioralLane(SignalSink sink) : base(sink)
    {
    }

    public override string Name => "behavioral";

    public override Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window,
        CancellationToken cancellationToken = default)
    {
        if (window.Count == 0)
        {
            EmitScore(0.0);
            return Task.CompletedTask;
        }

        // Compute behavioral indicators
        var timingEntropy = ComputeTimingEntropy(window);
        var pathDiversity = ComputePathDiversity(window);
        var requestRateScore = ComputeRequestRateScore(window);
        var scanScore = DetectScanPatterns(window);

        // Weighted combination (higher = more bot-like)
        var score = timingEntropy * 0.25 +      // Low entropy = bot-like
                    (1 - pathDiversity) * 0.25 + // Low diversity = bot-like
                    requestRateScore * 0.25 +    // High rate = bot-like
                    scanScore * 0.25;            // Sequential paths = bot-like

        // Emit component signals for observability
        Sink.Raise("behavioral.timing_entropy", timingEntropy.ToString("F4"));
        Sink.Raise("behavioral.path_diversity", pathDiversity.ToString("F4"));
        Sink.Raise("behavioral.request_rate", requestRateScore.ToString("F4"));
        Sink.Raise("behavioral.scan_score", scanScore.ToString("F4"));

        EmitScore(Math.Clamp(score, 0.0, 1.0));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Computes timing entropy using coefficient of variation.
    ///     Low CV (regular intervals) indicates bot-like behavior.
    /// </summary>
    private static double ComputeTimingEntropy(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 2) return 0.5;

        // Compute inter-request intervals
        var intervals = new List<double>();
        for (var i = 1; i < window.Count; i++)
        {
            var interval = (window[i].Timestamp - window[i - 1].Timestamp).TotalMilliseconds;
            if (interval > 0) intervals.Add(interval);
        }

        if (intervals.Count == 0) return 0.5;

        var mean = intervals.Average();
        if (mean < 1) return 0.5; // Avoid division issues

        var variance = intervals.Sum(x => Math.Pow(x - mean, 2)) / intervals.Count;
        var stdDev = Math.Sqrt(variance);
        var cv = stdDev / mean; // Coefficient of variation

        // Low CV (< 0.3) suggests regular bot-like timing
        // High CV (> 1.0) suggests human-like irregular timing
        // Return inverted so higher = more bot-like
        return Math.Clamp(1 - (cv / 2), 0.0, 1.0);
    }

    /// <summary>
    ///     Computes path diversity using Shannon entropy.
    ///     Low diversity indicates bot-like crawling patterns.
    /// </summary>
    private static double ComputePathDiversity(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 2) return 0.5;

        // Group paths and compute frequencies
        var pathCounts = window
            .Where(op => op.Path != null)
            .GroupBy(op => NormalizePath(op.Path!))
            .ToDictionary(g => g.Key, g => g.Count());

        var total = (double)window.Count;
        var entropy = 0.0;

        foreach (var count in pathCounts.Values)
        {
            var p = count / total;
            if (p > 0) entropy -= p * Math.Log2(p);
        }

        // Normalize by max possible entropy (log2 of unique paths)
        var maxEntropy = Math.Log2(pathCounts.Count);
        if (maxEntropy < 0.001) return 0.5;

        return Math.Clamp(entropy / maxEntropy, 0.0, 1.0);
    }

    /// <summary>
    ///     Computes request rate score based on requests per second.
    ///     High rate indicates bot-like behavior.
    /// </summary>
    private static double ComputeRequestRateScore(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 2) return 0.0;

        var timeSpan = (window[^1].Timestamp - window[0].Timestamp).TotalSeconds;
        if (timeSpan < 0.1) return 1.0; // Very fast = bot-like

        var rate = window.Count / timeSpan;

        // Thresholds: < 0.5 req/s = human, > 10 req/s = bot
        return Math.Clamp((rate - 0.5) / 9.5, 0.0, 1.0);
    }

    /// <summary>
    ///     Detects sequential/scanning path patterns.
    ///     Sequential probing (page1, page2, page3) indicates bots.
    /// </summary>
    private static double DetectScanPatterns(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 3) return 0.0;

        var sequentialCount = 0;
        var paths = window
            .Select(op => op.Path)
            .Where(p => p != null)
            .Cast<string>()
            .ToList();

        if (paths.Count < 3) return 0.0;

        for (var i = 2; i < paths.Count; i++)
        {
            // Check for numeric progression patterns
            var p1 = ExtractNumericSuffix(paths[i - 2]);
            var p2 = ExtractNumericSuffix(paths[i - 1]);
            var p3 = ExtractNumericSuffix(paths[i]);

            if (p1.HasValue && p2.HasValue && p3.HasValue)
            {
                if (p2 - p1 == 1 && p3 - p2 == 1)
                    sequentialCount++;
            }
        }

        return Math.Clamp(sequentialCount / (double)(paths.Count - 2), 0.0, 1.0);
    }

    private static string NormalizePath(string path)
    {
        // Remove query strings and normalize trailing slashes
        var idx = path.IndexOf('?');
        if (idx > 0) path = path[..idx];
        return path.TrimEnd('/').ToLowerInvariant();
    }

    private static int? ExtractNumericSuffix(string path)
    {
        // Extract trailing number from paths like /page/1, /item/123
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        return int.TryParse(parts[^1], out var num) ? num : null;
    }
}