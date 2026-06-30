using Mostlylucid.BotDetection.Orchestration.Lanes;
using Mostlylucid.BotDetection.Orchestration.Signals;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins <see cref="ReputationLane"/>'s cumulative-bad-behaviour arms
///     to the upstream-health gate. When upstream is healthy, the
///     existing 404 / 403 indicators fire and the score elevates;
///     when unhealthy, those arms suppress so origin-down windows don't
///     drag reputation toward "bot". Honeypot + 429 stay live in both.
/// </summary>
public class ReputationLaneUpstreamHealthTests
{
    private static OperationCompleteSignal Op(int statusCode, bool honeypot = false)
        => new()
        {
            Signature = "sig",
            RequestId = "rid",
            Timestamp = DateTimeOffset.UtcNow,
            Priority = 0,
            RequestRisk = 0,
            ResponseScore = 0,
            StatusCode = statusCode,
            ResponseBytes = 0,
            CombinedScore = 0,
            Honeypot = honeypot,
            TriggerSignals = new Dictionary<string, object>()
        };

    [Fact]
    public void Cumulative_404s_score_above_zero_when_upstream_healthy()
    {
        var window = new List<OperationCompleteSignal>
        {
            Op(404), Op(404), Op(404), Op(404), Op(404),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.True(score > 0.0, $"expected >0 when upstream healthy, got {score}");
    }

    [Fact]
    public void Cumulative_404s_score_drops_to_zero_when_upstream_unhealthy()
    {
        var window = new List<OperationCompleteSignal>
        {
            Op(404), Op(404), Op(404), Op(404), Op(404),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        var healthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        var unhealthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: false);

        Assert.Equal(0.0, unhealthy);
        Assert.True(healthy > unhealthy);
    }

    [Fact]
    public void Cumulative_403s_score_drops_when_upstream_unhealthy()
    {
        var window = new List<OperationCompleteSignal>
        {
            Op(403), Op(403), Op(403), Op(200), Op(200),
        };

        var healthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        var unhealthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: false);

        Assert.True(healthy > 0);
        Assert.Equal(0.0, unhealthy);
    }

    [Fact]
    public void Honeypot_hits_count_regardless_of_upstream_health()
    {
        // Honeypots are STYLOBOT's own traps -- they remain meaningful in
        // outage windows. This is the load-bearing regression guard for the
        // "keep honeypot intact" constraint.
        var window = new List<OperationCompleteSignal>
        {
            Op(200, honeypot: true), Op(200, honeypot: true), Op(200, honeypot: true),
        };

        var healthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        var unhealthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: false);

        Assert.Equal(healthy, unhealthy);
        Assert.True(healthy > 0);
    }

    [Fact]
    public void Rate_limited_429_responses_count_regardless_of_upstream_health()
    {
        // 429s are STYLOBOT-enforced rate limits, not origin shape. They
        // must remain meaningful in outage windows.
        var window = new List<OperationCompleteSignal>
        {
            Op(429), Op(429), Op(429), Op(200), Op(200),
        };

        var healthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        var unhealthy = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: false);

        Assert.Equal(healthy, unhealthy);
        Assert.True(healthy > 0);
    }
}
