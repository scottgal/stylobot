using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Detectors;

/// <summary>
///     Pins <see cref="HeuristicDetector.RunInference"/>'s upstream-health
///     gating. When upstream is healthy, 404-shaped response-history
///     features contribute to the bot probability; when unhealthy, those
///     same features stand down so origin-down windows don't drag the
///     model toward "bot". Honeypot + auth-struggle features must remain
///     active in both states (STYLOBOT-side evidence, not origin shape).
/// </summary>
public class HeuristicDetectorUpstreamHealthTests
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
    public void Response_404_features_elevate_score_when_upstream_healthy()
    {
        var detector = Build();
        var features = new Dictionary<string, float>
        {
            ["sigv:response_404_count"] = 1.0f,
            ["sigv:response_unique_404_paths"] = 1.0f,
            ["sigv:response_scan_pattern_detected"] = 1.0f,
        };

        var (_, healthyProb) = detector.RunInference(features, upstreamHealthy: true);
        var (_, unhealthyProb) = detector.RunInference(features, upstreamHealthy: false);

        Assert.True(healthyProb > unhealthyProb,
            $"healthy {healthyProb} should exceed unhealthy {unhealthyProb} for 404-only features");
    }

    [Fact]
    public void Response_404_features_do_not_elevate_score_when_upstream_unhealthy()
    {
        var detector = Build();
        var only404 = new Dictionary<string, float>
        {
            ["sigv:response_404_count"] = 1.0f,
            ["sigv:response_unique_404_paths"] = 1.0f,
            ["sigv:response_scan_pattern_detected"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, only404Prob) = detector.RunInference(only404, upstreamHealthy: false);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: false);

        Assert.Equal(emptyProb, only404Prob);
    }

    [Fact]
    public void Honeypot_feature_still_fires_when_upstream_unhealthy()
    {
        // Honeypots are STYLOBOT's own traps. They must remain active in
        // outage windows -- there's no excuse for hitting /.env.
        var detector = Build();
        var honeypot = new Dictionary<string, float>
        {
            ["sigv:response_honeypot_hits"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, honeypotProb) = detector.RunInference(honeypot, upstreamHealthy: false);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: false);

        Assert.True(honeypotProb > emptyProb,
            $"honeypot feature must still elevate under outage: {honeypotProb} vs {emptyProb}");
    }

    [Fact]
    public void Auth_struggle_feature_still_fires_when_upstream_unhealthy()
    {
        // Auth-struggle is a STYLOBOT-side observation independent of
        // upstream return codes; it must remain active.
        var detector = Build();
        var authStruggle = new Dictionary<string, float>
        {
            ["sigv:response_auth_failures"] = 1.0f,
        };
        var empty = new Dictionary<string, float>();

        var (_, authProb) = detector.RunInference(authStruggle, upstreamHealthy: false);
        var (_, emptyProb) = detector.RunInference(empty, upstreamHealthy: false);

        Assert.True(authProb > emptyProb);
    }
}