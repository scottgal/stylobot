namespace Mostlylucid.BotDetection.Models;

/// <summary>How transport fingerprint headers (X-JA3-*, X-Client-TLS-*, X-HTTP2-*, X-QUIC-*, X-TCP-*) are trusted.</summary>
public enum TransportTrustMode
{
    /// <summary>Trust if peer is allowlisted or loopback/private. Default.</summary>
    Auto,

    /// <summary>Trust only if the immediate peer is in TrustedProxyIps.</summary>
    Strict,

    /// <summary>Trust all transport headers regardless of peer (legacy behaviour; logs a startup warning).</summary>
    Off
}

/// <summary>
/// Controls whether edge-injected transport fingerprint headers are trusted.
/// Bound at BotDetection:TransportTrust.
/// </summary>
public sealed class TransportTrustOptions
{
    /// <summary>Trust mode. Default Auto.</summary>
    public TransportTrustMode Mode { get; set; } = TransportTrustMode.Auto;

    /// <summary>CIDRs / IPs of trusted reverse proxies, e.g. ["10.0.0.0/8", "203.0.113.5"].</summary>
    public List<string> TrustedProxyIps { get; set; } = [];

    /// <summary>Auto mode: trust headers when the immediate peer is loopback or RFC1918/RFC4193 private. Default true.</summary>
    public bool TrustPrivatePeers { get; set; } = true;
}
