using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Pins <see cref="HeuristicDetector.RunInference"/>'s gateway-warmup
///     gating. When the gateway is in cold-start warmup, behavioural
///     aggregate features (sigv:* session+cluster+intent+history, stat:*
///     detector summaries, result:* pipeline feedback) stand down because
///     the underlying aggregates are sampled from too few requests to score
///     reliably. Honeypot + identity / UA / header features must remain
///     active in both states (single-request evidence, not multi-request
///     aggregates).
/// </summary>
public class HeuristicDetectorWarmupTests
{
    private static HeuristicDetector Build()
        => new(
            NullLogger<HeuristicDetector>.Instance,
            Options.Create(new BotDetectionOptions
            {
                AiDetection = new AiDetectionOptions
                {
                    Heuristic = new HeuristicOptions { Enabled = true }
                }
            }));

    [Fact]
    public void Behavioural_aggregate_features_elevate_score_when_warmed_up()
    {
        var detector = Build();
        var features = new Dictionary<string, float>
        {
            ["sigv:session_velocity_magnitude"] = 1.0f,
            ["sigv:session_self_similarity"] = 1.0f,
            ["sigv:cluster_avg_similarity"] = 1.0f,
            ["stat:detector_max"] = 1.0f,
            ["result:bot_probability"] = 1.0f,
        };

        var (_, warmedProb) = detector.RunInference(features, upstreamHealthy: true, gatewayWarming: false);
        var (_, warmingProb) = detector.RunInference(features, upstreamHealthy: true, gatewayWarming: true);

        Assert.True(warmedProb > warmingProb,
            $"warmed {warmedProb} should exceed warming {warmingProb} for behavioural-only features");
    }

    [Fact]
    public void Behavioural_aggregate_features_do_not_elevate_score_during_warmup()
    {
        var detector = Build();
        var behaviouralOnly = new Dictionary<string, float>
        {
            ["sigv:session_velocity_magnitude"] = 1.0f,
            ["sigv:session_self_similarity"] = 1.0f,
            ["sigv:cluster_avg_similarity"] = 1.0f,
            ["stat:detector_max"] = 1.0f,
            ["result:bot_probability"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, behaviouralProb) = detector.RunInference(behaviouralOnly, upstreamHealthy: true, gatewayWarming: true);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: true, gatewayWarming: true);

        Assert.Equal(emptyProb, behaviouralProb);
    }

    [Fact]
    public void Honeypot_feature_still_fires_during_warmup()
    {
        // Honeypots are STYLOBOT-side evidence sourced from the current
        // request, not multi-request aggregates. They must remain active
        // even when the gateway is in cold-start warmup.
        var detector = Build();
        var honeypot = new Dictionary<string, float>
        {
            ["sigv:response_honeypot_hits"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, honeypotProb) = detector.RunInference(honeypot, upstreamHealthy: true, gatewayWarming: true);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: true, gatewayWarming: true);

        Assert.True(honeypotProb > emptyProb,
            $"honeypot feature must still elevate during warmup: {honeypotProb} vs {emptyProb}");
    }

    [Fact]
    public void Identity_UA_features_still_fire_during_warmup()
    {
        // UA / header identity features score from the first observation.
        // They must remain active during warmup so obvious bots are still
        // caught while the behavioural arms are standing down.
        var detector = Build();
        var uaFeatures = new Dictionary<string, float>
        {
            ["ua:contains_bot"] = 1.0f,
            ["ua:headless"] = 1.0f,
            ["ua:phantomjs"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, uaProb) = detector.RunInference(uaFeatures, upstreamHealthy: true, gatewayWarming: true);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: true, gatewayWarming: true);

        Assert.True(uaProb > emptyProb,
            $"UA identity features must still fire during warmup: {uaProb} vs {emptyProb}");
    }

    [Fact]
    public void Status_gated_features_still_fire_during_warmup_when_upstream_is_healthy()
    {
        // The two gates compose independently. Warmup gates behavioural
        // arms, not the status-gated 404 arms (those are governed by
        // upstream-health). A warmup-active gateway with a healthy upstream
        // should still elevate on status-derived features.
        var detector = Build();
        var statusFeatures = new Dictionary<string, float>
        {
            ["sigv:response_404_count"] = 1.0f,
            ["sigv:response_unique_404_paths"] = 1.0f,
            ["sigv:response_scan_pattern_detected"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, statusProb) = detector.RunInference(statusFeatures, upstreamHealthy: true, gatewayWarming: true);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: true, gatewayWarming: true);

        Assert.True(statusProb > emptyProb);
    }
}