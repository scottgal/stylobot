using Mostlylucid.BotDetection.Orchestration.Lanes;
using Mostlylucid.BotDetection.Orchestration.Signals;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins <see cref="ReputationLane.ComputeCumulativeBadBehavior"/>'s
///     per-request <c>FromUpstream</c> gate. When stylobot itself synthesised
///     a 403 (policy block), 404 (honeypot), or 429 (throttle), the
///     status-code-derived bad-behaviour arms MUST suppress for that
///     specific operation -- otherwise stylobot's own enforcement
///     responses feed back as bot evidence on the visitor's next request
///     (closed-loop feedback). Honeypot hits still count via the dedicated
///     <c>Honeypot</c> path.
/// </summary>
public class ReputationLaneFromUpstreamTests
{
    private static OperationCompleteSignal Op(
        int statusCode,
        bool fromUpstream = true,
        bool honeypot = false)
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
            FromUpstream = fromUpstream,
            CombinedScore = 0,
            Honeypot = honeypot,
            TriggerSignals = new Dictionary<string, object>()
        };

    [Fact]
    public void Cumulative_403s_score_drops_when_responses_were_synthesised_by_stylobot()
    {
        // Stylobot-block 403s in the window must not count -- otherwise our
        // own block response feeds back as evidence the visitor was bad.
        var window = new List<OperationCompleteSignal>
        {
            Op(403, fromUpstream: false),
            Op(403, fromUpstream: false),
            Op(403, fromUpstream: false),
            Op(200), Op(200),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Cumulative_404s_score_drops_when_responses_were_synthesised_by_stylobot()
    {
        // Stylobot honeypot 404s in the window must not count via the
        // status-code path. (They DO still count via the Honeypot flag --
        // see the honeypot regression guard below.)
        var window = new List<OperationCompleteSignal>
        {
            Op(404, fromUpstream: false),
            Op(404, fromUpstream: false),
            Op(404, fromUpstream: false),
            Op(404, fromUpstream: false),
            Op(404, fromUpstream: false),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Cumulative_429s_score_drops_when_responses_were_synthesised_by_stylobot()
    {
        // Stylobot policy-throttle 429s in the window must not count --
        // that's our enforcement, not a peer service rate-limiting the
        // visitor.
        var window = new List<OperationCompleteSignal>
        {
            Op(429, fromUpstream: false),
            Op(429, fromUpstream: false),
            Op(429, fromUpstream: false),
            Op(200), Op(200),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Cumulative_upstream_404s_still_count_when_from_upstream_true()
    {
        // Regression guard: an upstream 4xx is still scanner-shape evidence
        // when the visitor genuinely probed missing paths.
        var window = new List<OperationCompleteSignal>
        {
            Op(404, fromUpstream: true),
            Op(404, fromUpstream: true),
            Op(404, fromUpstream: true),
            Op(404, fromUpstream: true),
            Op(404, fromUpstream: true),
            Op(200), Op(200), Op(200), Op(200), Op(200),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.True(score > 0.0, $"expected upstream 4xx to count, got {score}");
    }

    [Fact]
    public void Honeypot_hits_count_regardless_of_from_upstream()
    {
        // Honeypot path detection scores via the dedicated Honeypot flag,
        // NOT the status code. Even when stylobot's honeypot middleware
        // synthesised the 404 (FromUpstream=false), the Honeypot=true
        // pathway must keep firing -- that's the canonical honeypot
        // signal pathway the task description explicitly preserves.
        var window = new List<OperationCompleteSignal>
        {
            Op(404, fromUpstream: false, honeypot: true),
            Op(404, fromUpstream: false, honeypot: true),
            Op(404, fromUpstream: false, honeypot: true),
        };

        var score = ReputationLane.ComputeCumulativeBadBehavior(window, upstreamHealthy: true);
        Assert.True(score > 0.0,
            $"honeypot signal pathway must remain active under FromUpstream=false, got {score}");
    }

    [Fact]
    public void FromUpstream_defaults_true_for_back_compat()
    {
        // Back-compat: existing OperationCompleteSignal producers that
        // don't yet stamp FromUpstream keep their pre-fix behaviour
        // (treated as upstream-derived, so the status arms still fire).
        var op = new OperationCompleteSignal
        {
            Signature = "sig",
            RequestId = "rid",
            Timestamp = DateTimeOffset.UtcNow,
            Priority = 0,
            RequestRisk = 0,
            ResponseScore = 0,
            StatusCode = 404,
            ResponseBytes = 0,
            CombinedScore = 0,
            Honeypot = false,
            TriggerSignals = new Dictionary<string, object>()
            // FromUpstream omitted -- default is true
        };
        Assert.True(op.FromUpstream);
    }
}
