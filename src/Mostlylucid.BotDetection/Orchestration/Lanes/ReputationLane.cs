using Mostlylucid.BotDetection.Lifecycle;
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
    private readonly GatewayWarmupGate? _gatewayWarmup;

    public ReputationLane(
        SignalSink sink,
        string coordinatorKey,
        UpstreamHealthGate? upstreamHealth = null,
        GatewayWarmupGate? gatewayWarmup = null)
        : base(sink, coordinatorKey)
    {
        _upstreamHealth = upstreamHealth;
        _gatewayWarmup = gatewayWarmup;
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

        // Compute reputation indicators. Two cold-start gates compose by OR:
        //   * Upstream-health stands down 404 / 403 bad-behaviour indicators
        //     when origin is cold-starting or down (gateway hands back 4xx
        //     via YARP; we cannot tell scanner shape from outage shape).
        //   * Gateway-warmup stands down the trend / consistency arms
        //     (which compare across a window of recent requests) when
        //     stylobot itself is in cold-start warmup -- those arms produce
        //     spurious "bot-like consistency" verdicts off the first
        //     dozen requests where every signature looks similar by
        //     chance. Reputation lane emits 0.0 entirely while warming so
        //     the upstream scorer doesn't compound cold-start noise.
        // Honeypot hits + 429s remain meaningful regardless of either gate.
        if (_gatewayWarmup is not null && !_gatewayWarmup.IsWarmedUp())
        {
            EmitMetric("warmup_skipped", "1");
            EmitScore(0.0);
            return Task.CompletedTask;
        }

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
    ///     Two gates compose per status-code arm:
    ///     <list type="bullet">
    ///         <item>
    ///             <paramref name="upstreamHealthy"/> suppresses the 404 /
    ///             403 / 429 arms across the WHOLE window during outage
    ///             windows -- we can't tell scanner shape from "everything
    ///             is 4xx" upstream-down shape.
    ///         </item>
    ///         <item>
    ///             <see cref="OperationCompleteSignal.FromUpstream"/>
    ///             suppresses the per-request status-derived arms when
    ///             STYLOBOT itself set the status (honeypot path 404,
    ///             policy block 403, etc.) so our own enforcement
    ///             responses don't feed back as bot evidence on the next
    ///             request (closed-loop feedback). Per "ONLY upstream
    ///             status codes should be factored in".
    ///         </item>
    ///     </list>
    ///     Honeypot hits remain meaningful in all states because they
    ///     score via the dedicated <see cref="OperationCompleteSignal.Honeypot"/>
    ///     pathway, not the status-code pathway.
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

            // Per-request status arms gate on BOTH upstream health AND
            // whether this specific response came from upstream. If
            // stylobot synthesised the status (block / shed / honeypot
            // 404), the status code carries no information about the
            // visitor's intent -- only about our own enforcement choice.
            var statusArmsActive = upstreamHealthy && op.FromUpstream;
            if (statusArmsActive)
            {
                // 404 responses (probing)
                if (op.StatusCode == 404) badIndicators++;

                // 403 responses (blocked/forbidden)
                if (op.StatusCode == 403) badIndicators++;

                // 429 responses (rate limited) -- when from upstream, this
                // is a peer service rate-limiting our visitor and counts
                // as bad-behaviour evidence. When stylobot itself sent
                // the 429 (policy throttle), suppress to avoid the
                // closed-loop feedback.
                if (op.StatusCode == 429) badIndicators += 2;
            }

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