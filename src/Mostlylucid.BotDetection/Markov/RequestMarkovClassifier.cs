using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Markov;

/// <summary>
///     Classifies an HTTP request into a <see cref="RequestState"/> for Markov chain tracking.
///     Shared by <c>SessionVectorContributor</c> and <c>ContentSequenceContributor</c>
///     so both use identical classification logic.
/// </summary>
public static class RequestMarkovClassifier
{
    /// <summary>
    ///     Maps the current request into a Markov state based on transport, path, and response signals.
    /// </summary>
    public static RequestState Classify(BlackboardState state)
    {
        var context = state.HttpContext;
        var request = context.Request;

        // Transport-level classification (highest priority)
        var isSignalR = state.GetSignal<bool?>(SignalKeys.TransportIsSignalR) ?? false;
        var isUpgrade = state.GetSignal<bool?>(SignalKeys.TransportIsUpgrade) ?? false;

        return ClassifyCore(
            context,
            request,
            isSignalR,
            isUpgrade,
            upstreamHealthy: state.GetSignal<bool?>(SignalKeys.UpstreamHealthy) ?? true,
            gatewayWarming: state.GetSignal<bool?>(SignalKeys.GatewayWarmup) ?? false,
            fromUpstream: state.GetSignal<bool?>(SignalKeys.ResponseFromUpstream) ?? true,
            protocolClass: state.GetSignal<string>(SignalKeys.TransportProtocolClass));
    }

    /// <summary>
    ///     Sink-native <see cref="Classify(BlackboardState)"/> overload used by
    ///     native atoms that don't have a <see cref="BlackboardState"/> in hand.
    ///     Reads the same five transport/response signals off the sink instead
    ///     of the blackboard dictionary.
    /// </summary>
    public static RequestState Classify(HttpContext context, SignalSink sink)
    {
        var request = context.Request;
        return ClassifyCore(
            context,
            request,
            isSignalR: sink.ReadBoolHint(SignalKeys.TransportIsSignalR),
            isUpgrade: sink.ReadBoolHint(SignalKeys.TransportIsUpgrade),
            upstreamHealthy: sink.ReadBoolHint(SignalKeys.UpstreamHealthy, fallback: true),
            gatewayWarming: sink.ReadBoolHint(SignalKeys.GatewayWarmup),
            fromUpstream: sink.ReadBoolHint(SignalKeys.ResponseFromUpstream, fallback: true),
            protocolClass: sink.ReadHint(SignalKeys.TransportProtocolClass));
    }

    private static RequestState ClassifyCore(
        HttpContext context,
        HttpRequest request,
        bool isSignalR,
        bool isUpgrade,
        bool upstreamHealthy,
        bool gatewayWarming,
        bool fromUpstream,
        string? protocolClass)
    {

        if (isSignalR) return RequestState.SignalR;
        if (isUpgrade) return RequestState.WebSocket;

        var acceptHeader = request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return RequestState.ServerSentEvent;

        // Response-based classification.
        // Three gates compose by OR here:
        //   * Upstream-health: when origin is cold-starting or down, the
        //     gateway returns 4xx for everything via YARP. Treating those
        //     status codes as Markov-state evidence would bake "scanner" /
        //     "auth-attempt" shape into the session vector.
        //   * Gateway-warmup: when stylobot itself just booted (process
        //     uptime / total samples under floor), behavioural classifiers
        //     downstream of this state assignment can't yet score reliably;
        //     keep observed shape as PageView so we don't lock in noisy
        //     cold-start centroid samples.
        //   * Response-from-upstream (per-request): when STYLOBOT itself
        //     set the status (block 403, honeypot 404, throttle 429), the
        //     Markov state must not classify as AuthAttempt / NotFound or
        //     we feed our own enforcement response back into the session
        //     vector as scanner / brute-force shape -- locking the visitor
        //     at 100% bot from a single enforcement action.
        // Any gate "cold" → demote to PageView so persisted centroids
        // don't bake outage / cold-start / enforcement shape (per
        // feedback_centroid_learning_feedback_loop).
        var statusCode = context.Response.StatusCode;
        if (upstreamHealthy && !gatewayWarming && fromUpstream)
        {
            if (statusCode == 401 || statusCode == 403)
                return RequestState.AuthAttempt;
            if (statusCode == 404)
                return RequestState.NotFound;
        }

        // Content-type classification from transport signal
        if (protocolClass == "api") return RequestState.ApiCall;
        if (protocolClass == "static") return RequestState.StaticAsset;

        // Method + content heuristics
        if (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method))
        {
            var contentType = request.ContentType ?? "";
            if (contentType.Contains("form", StringComparison.OrdinalIgnoreCase))
                return RequestState.FormSubmit;
            if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                return RequestState.ApiCall;
        }

        // Path heuristics
        var path = request.Path.Value ?? "";
        if (path.Contains("/search", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/find", StringComparison.OrdinalIgnoreCase) ||
            request.QueryString.Value?.Contains("q=", StringComparison.OrdinalIgnoreCase) == true)
            return RequestState.Search;

        // Sec-Fetch-Dest for page vs asset
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (secFetchDest is "script" or "style" or "image" or "font")
            return RequestState.StaticAsset;

        return RequestState.PageView;
    }

    /// <summary>
    ///     Returns true if this request is a browser prefetch/preload resource hint.
    ///     Prefetch requests never count toward sequence divergence regardless of their Markov state.
    /// </summary>
    public static bool IsPrefetchRequest(HttpRequest request)
    {
        // Chromium/Firefox: Purpose: prefetch header (older)
        var purpose = request.Headers["Purpose"].FirstOrDefault();
        if (string.Equals(purpose, "prefetch", StringComparison.OrdinalIgnoreCase))
            return true;

        // Chrome 112+: Sec-Purpose: prefetch (Fetch Metadata equivalent)
        var secPurpose = request.Headers["Sec-Purpose"].FirstOrDefault();
        if (string.Equals(secPurpose, "prefetch", StringComparison.OrdinalIgnoreCase))
            return true;

        // Sec-Fetch-Mode: no-cors + Sec-Fetch-Dest: document = browser-initiated prefetch
        var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault();
        var secFetchDest = request.Headers["Sec-Fetch-Dest"].FirstOrDefault();
        if (string.Equals(secFetchMode, "no-cors", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(secFetchDest, "document", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
