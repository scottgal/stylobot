using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration.Lanes;

/// <summary>
///     Reputation analysis lane - maintains historical scoring and trend analysis.
/// </summary>
internal sealed class ReputationLane : AnalysisLaneBase
{
    private const double DecayFactor = 0.95; // Per-request decay
    private readonly UpstreamHealthGate? _upstreamHealth;

    public ReputationLane(SignalSink sink, string coordinatorKey, UpstreamHealthGate? upstreamHealth = null)
        : base(sink, coordinatorKey)
    {
        _upstreamHealth = upstreamHealth;
    }

    public override string Name => "reputation";

    public override Task AnalyzeAsync(IReadOnlyList<OperationCompleteSignal> window,
        CancellationToken cancellationToken = default)
    {
        if (window.Count == 0)
        {
            EmitScore(0.0);
            return Task.CompletedTask;
        }

        // Compute reputation indicators. Upstream-health-aware bad-behaviour
        // counter stands down 404/403 indicators when origin is cold-starting
        // or down (the gateway hands back 4xx via YARP and we cannot tell
        // scanner shape from outage shape). Honeypot hits + 429s remain
        // meaningful regardless.
        var upstreamHealthy = _upstreamHealth?.IsUpstreamHealthy() ?? true;
        var decayedScore = ComputeDecayedHistoricalScore(window);
        var trendScore = ComputeTrendScore(window);
        var badBehaviorScore = ComputeCumulativeBadBehavior(window, upstreamHealthy);
        var consistencyScore = ComputeConsistencyScore(window);

        // Weighted combination (higher = more bot-like)
        var score = decayedScore * 0.35 +
                    trendScore * 0.25 +
                    badBehaviorScore * 0.25 +
                    consistencyScore * 0.15;

        // Emit component signals for observability (scoped to this coordinator via EmitMetric)
        EmitMetric("decayed_score", decayedScore.ToString("F4"));
        EmitMetric("trend", trendScore.ToString("F4"));
        EmitMetric("bad_behavior", badBehaviorScore.ToString("F4"));
        EmitMetric("consistency", consistencyScore.ToString("F4"));

        EmitScore(Math.Clamp(score, 0.0, 1.0));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Computes historical score with exponential time decay.
    ///     Recent behavior matters more than old behavior.
    /// </summary>
    private static double ComputeDecayedHistoricalScore(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count == 0) return 0.0;

        var weightedSum = 0.0;
        var weightSum = 0.0;
        var weight = 1.0;

        // Process from newest to oldest (reverse order)
        for (var i = window.Count - 1; i >= 0; i--)
        {
            weightedSum += window[i].CombinedScore * weight;
            weightSum += weight;
            weight *= DecayFactor;
        }

        return weightSum > 0 ? weightedSum / weightSum : 0.0;
    }

    /// <summary>
    ///     Computes trend score - is behavior getting worse or better?
    ///     Deteriorating behavior (increasing scores) indicates suspicious pattern.
    /// </summary>
    private static double ComputeTrendScore(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 3) return 0.5;

        // Split window into halves
        var midpoint = window.Count / 2;
        var firstHalf = window.Take(midpoint).ToList();
        var secondHalf = window.Skip(midpoint).ToList();

        var firstAvg = firstHalf.Average(op => op.CombinedScore);
        var secondAvg = secondHalf.Average(op => op.CombinedScore);

        // Positive delta = getting worse = more bot-like
        var delta = secondAvg - firstAvg;

        // Normalize: -0.5 to +0.5 delta maps to 0 to 1 score
        return Math.Clamp((delta + 0.5) / 1.0, 0.0, 1.0);
    }

    /// <summary>
    ///     Tracks cumulative bad behavior indicators.
    ///     High-risk requests, 404s, blocked responses indicate bots.
    ///     <paramref name="upstreamHealthy"/> suppresses the 404 / 403 arms
    ///     during outage windows; 429 and honeypot stay live because they
    ///     are STYLOBOT enforcement signals, not origin-shape signals.
    /// </summary>
    internal static double ComputeCumulativeBadBehavior(
        IReadOnlyList<OperationCompleteSignal> window,
        bool upstreamHealthy)
    {
        if (window.Count == 0) return 0.0;

        var badIndicators = 0;

        foreach (var op in window)
        {
            // High risk request
            if (op.RequestRisk > 0.7) badIndicators++;

            if (upstreamHealthy)
            {
                // 404 responses (probing)
                if (op.StatusCode == 404) badIndicators++;

                // 403 responses (blocked/forbidden)
                if (op.StatusCode == 403) badIndicators++;
            }

            // 429 responses (rate limited) - meaningful regardless of upstream health
            if (op.StatusCode == 429) badIndicators += 2;

            // Honeypot hit - STYLOBOT's own trap, always meaningful
            if (op.Honeypot) badIndicators += 3;
        }

        // Normalize by window size (max 3 bad indicators per request)
        return Math.Clamp(badIndicators / (double)(window.Count * 3), 0.0, 1.0);
    }

    /// <summary>
    ///     Computes consistency score - bots often have very consistent behavior patterns.
    ///     Low variance in scores indicates automated behavior.
    /// </summary>
    private static double ComputeConsistencyScore(IReadOnlyList<OperationCompleteSignal> window)
    {
        if (window.Count < 3) return 0.5;

        var scores = window.Select(op => op.CombinedScore).ToList();
        var mean = scores.Average();

        if (mean < 0.01) return 0.0; // Avoid division issues

        var variance = scores.Sum(s => Math.Pow(s - mean, 2)) / scores.Count;
        var stdDev = Math.Sqrt(variance);
        var cv = stdDev / mean;

        // Low CV = highly consistent = more bot-like
        // Return inverted so higher = more bot-like
        return Math.Clamp(1 - cv, 0.0, 1.0);
    }
}