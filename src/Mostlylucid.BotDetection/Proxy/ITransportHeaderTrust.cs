using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>Outcome of a transport-header trust evaluation.</summary>
public readonly record struct TransportTrustResult(bool Trusted, string Reason);

/// <summary>
/// Decides whether edge-injected transport fingerprint headers should be trusted,
/// based on the immediate TCP peer and configured policy.
/// </summary>
public interface ITransportHeaderTrust
{
    /// <summary>Legacy blackboard-path overload. Prefer the sink-based overload from new atoms.</summary>
    TransportTrustResult Evaluate(BlackboardState state);

    /// <summary>
    ///     Sink-native evaluation. Called from native atoms that don't have
    ///     a <see cref="BlackboardState"/> in hand. Writes the same
    ///     transport.headers_trusted / transport.trust_reason signals onto
    ///     the sink instead of the blackboard.
    /// </summary>
    TransportTrustResult Evaluate(HttpContext context, SignalSink sink, string sessionId);
}
