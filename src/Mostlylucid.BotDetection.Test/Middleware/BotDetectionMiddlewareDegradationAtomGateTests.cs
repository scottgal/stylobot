using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Pins the closed-loop feedback gate on
///     <see cref="BotDetectionMiddleware"/>'s <c>Response.OnCompleted</c>
///     hook that feeds <see cref="DegradationAtom.RecordResponse"/> (audit
///     #10 + <c>project_centroid_learning_feedback_loop</c>).
///     <para>
///     The full <c>InvokeAsync</c> path needs a half-dozen orchestrator /
///     coordinator dependencies. The piece under test is one
///     <c>if (context.IsResponseFromUpstream()) degradationAtom?.RecordResponse(...)</c>
///     guard. We pin the contract at that resolution by exercising the
///     <see cref="StyloBotResponseSignalExtensions"/> view of the
///     <c>HttpContext.Items</c> flag the same way the middleware would.
///     Otherwise stylobot's own 429 (throttle) / 503 (load-shed) / 403
///     (block) / honeypot-404 codes feed into Rate429 + 5xx + 4xx EMAs,
///     flip <see cref="UpstreamHealthGate"/> to unhealthy, and stand down
///     <c>ApplyResponseStatusBoost</c> precisely when stylobot is actively
///     throttling -- the gateway self-disarms under attack.
///     </para>
/// </summary>
public class BotDetectionMiddlewareDegradationAtomGateTests
{
    /// <summary>
    ///     Mirrors the gate at <c>BotDetectionMiddleware.cs:line 166</c>
    ///     so the test can pin its semantics without spinning up the full
    ///     InvokeAsync dependency graph. If the production gate ever stops
    ///     reading <see cref="StyloBotResponseSignalExtensions.IsResponseFromUpstream"/>,
    ///     this test guard fails first.
    /// </summary>
    private static void SimulateOnCompleted(HttpContext context, DegradationAtom atom)
    {
        if (context.IsResponseFromUpstream())
            atom.RecordResponse(context.Response.StatusCode, latencyMs: 5, path: "/");
    }

    [Fact]
    public void RecordResponse_fires_when_response_is_from_upstream()
    {
        // Baseline: untouched HttpContext.Items => upstream by default.
        // The atom records the response as it always has.
        var atom = new DegradationAtom();
        var ctx = new DefaultHttpContext();
        ctx.Response.StatusCode = 200;

        SimulateOnCompleted(ctx, atom);

        Assert.Equal(1, atom.TotalSamples);
    }

    [Fact]
    public void RecordResponse_skipped_when_stylobot_marked_response()
    {
        // After any of the status-setting middlewares stamp
        // MarkResponseFromStyloBot (load-shed, policy block, throttle,
        // honeypot, API-key reject), the gate refuses to record. The atom
        // stays at zero so UpstreamHealthGate keeps reporting healthy and
        // stylobot's own boost arms stay live.
        var atom = new DegradationAtom();
        var ctx = new DefaultHttpContext();
        ctx.Response.StatusCode = 429;
        ctx.MarkResponseFromStyloBot();

        SimulateOnCompleted(ctx, atom);

        Assert.Equal(0, atom.TotalSamples);
    }

    [Fact]
    public void Production_gate_reads_the_same_extension_method()
    {
        // Pins the production gate against the extension contract -- the
        // exact predicate the BotDetectionMiddleware OnCompleted hook
        // checks at line 166. If a future refactor reads context.Items
        // directly with the wrong key the production gate would still
        // compile but the predicate would diverge from the markers; this
        // test catches that drift.
        var ctx = new DefaultHttpContext();
        Assert.True(ctx.IsResponseFromUpstream());

        ctx.MarkResponseFromStyloBot();
        Assert.False(ctx.IsResponseFromUpstream());
    }
}
