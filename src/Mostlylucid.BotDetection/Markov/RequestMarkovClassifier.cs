using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

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

        if (isSignalR) return RequestState.SignalR;
        if (isUpgrade) return RequestState.WebSocket;

        var acceptHeader = request.Headers.Accept.ToString();
        if (acceptHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return RequestState.ServerSentEvent;

        // Response-based classification.
        // Two cold-start gates compose by OR here:
        //   * Upstream-health: when origin is cold-starting or down, the
        //     gateway returns 4xx for everything via YARP. Treating those
        //     status codes as Markov-state evidence would bake "scanner" /
        //     "auth-attempt" shape into the session vector.
        //   * Gateway-warmup: when stylobot itself just booted (process
        //     uptime / total samples under floor), behavioural classifiers
        //     downstream of this state assignment can't yet score reliably;
        //     keep observed shape as PageView so we don't lock in noisy
        //     cold-start centroid samples.
        // Either gate "cold" → demote to PageView so persisted centroids
        // don't bake outage/cold-start shape (per
        // feedback_centroid_learning_feedback_loop).
        var statusCode = context.Response.StatusCode;
        var upstreamHealthy = state.GetSignal<bool?>(SignalKeys.UpstreamHealthy) ?? true;
        var gatewayWarming = state.GetSignal<bool?>(SignalKeys.GatewayWarmup) ?? false;
        if (upstreamHealthy && !gatewayWarming)
        {
            if (statusCode == 401 || statusCode == 403)
                return RequestState.AuthAttempt;
            if (statusCode == 404)
                return RequestState.NotFound;
        }

        // Content-type classification from transport signal
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);
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
