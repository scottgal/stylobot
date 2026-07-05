using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Helpers stylobot's own response-setting middlewares call to mark
///     responses they synthesised so detector arms downstream don't read
///     stylobot's own enforcement codes as additional bot evidence.
/// </summary>
/// <remarks>
///     <para>
///         The detection pipeline contains five sites that derive bot
///         evidence from <c>HttpContext.Response.StatusCode</c> or from
///         historical response status codes
///         (<see cref="BotDetectionMiddleware"/>'s
///         <c>ApplyResponseStatusBoost</c>;
///         <c>ResponseBehaviorContributor.AnalyzeScanPatterns</c> and
///         <c>AnalyzeAuthStruggle</c>;
///         <c>HeuristicDetector</c>'s 404-derived feature weights;
///         <c>RequestMarkovClassifier</c> + <c>SessionVector</c> Markov
///         <c>NotFound</c> transitions). When stylobot's own load-shed /
///         policy block / throttle / honeypot middleware sets the response
///         code, those sites would otherwise feed the gateway's own
///         enforcement action back as bot evidence and lock the visitor at
///         100% bot from a single shed or block.
///     </para>
///     <para>
///         Hosts that wire custom action middlewares (commercial
///         simulation packs, holodeck responders, etc.) should call
///         <see cref="MarkResponseFromStyloBot"/> at the moment they set
///         the response status code. The flag is also mirrored onto the
///         <see cref="Mostlylucid.BotDetection.Models.SignalKeys.ResponseFromUpstream"/>
///         signal so the orchestrator's blackboard sees the same gate
///         (default semantics: absent = upstream, so existing FOSS hosts
///         that don't yet stamp keep their pre-fix behaviour).
///     </para>
/// </remarks>
public static class StyloBotResponseSignalExtensions
{
    /// <summary>
    ///     Marks the current response as stylobot-synthesised. Call from
    ///     any middleware that writes a status code to the response without
    ///     forwarding to upstream (load-shed, policy block / challenge /
    ///     throttle, honeypot, API-key rejection, simulation-pack responder).
    /// </summary>
    /// <param name="context">The current request's HttpContext.</param>
    public static void MarkResponseFromStyloBot(this HttpContext context)
    {
        if (context is null) return;
        context.Items[BotDetectionMiddleware.ResponseFromUpstreamKey] = false;
    }

    /// <summary>
    ///     Returns <c>true</c> when the response status code on the current
    ///     request came from upstream (no stylobot middleware claimed it),
    ///     <c>false</c> when STYLOBOT itself set the status. Absent =
    ///     upstream (back-compat default). Detector arms that derive bot
    ///     evidence from the status code call this and stand down on
    ///     <c>false</c>.
    /// </summary>
    public static bool IsResponseFromUpstream(this HttpContext context)
    {
        if (context is null) return true;
        if (context.Items.TryGetValue(BotDetectionMiddleware.ResponseFromUpstreamKey, out var raw)
            && raw is bool flag)
            return flag;
        return true;
    }
}
