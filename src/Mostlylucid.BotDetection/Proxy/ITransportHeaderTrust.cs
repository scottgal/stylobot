using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>Outcome of a transport-header trust evaluation.</summary>
public readonly record struct TransportTrustResult(bool Trusted, string Reason);

/// <summary>
/// Decides whether edge-injected transport fingerprint headers should be trusted,
/// based on the immediate TCP peer and configured policy.
/// </summary>
public interface ITransportHeaderTrust
{
    /// <summary>Evaluate trust and write transport.headers_trusted / transport.trust_reason signals.</summary>
    TransportTrustResult Evaluate(BlackboardState state);
}
