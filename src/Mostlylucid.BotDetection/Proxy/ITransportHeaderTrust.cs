using Microsoft.AspNetCore.Http;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>Outcome of a transport-header trust evaluation.</summary>
public readonly record struct TransportTrustResult(bool Trusted, string Reason);

/// <summary>
///     Decides whether edge-injected transport fingerprint headers should be
///     trusted, based on the immediate TCP peer and configured policy.
/// </summary>
public interface ITransportHeaderTrust
{
    /// <summary>
    ///     Sink-native evaluation. Writes
    ///     <c>transport.headers_trusted</c> / <c>transport.trust_reason</c>
    ///     signals onto the per-request sink.
    /// </summary>
    TransportTrustResult Evaluate(HttpContext context, SignalSink sink, string sessionId);
}
