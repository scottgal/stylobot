namespace Mostlylucid.BotDetection.Domains;

/// <summary>Well-known HttpContext.Items keys used across the detection pipeline.</summary>
public static class HttpContextItemKeys
{
    public const string Domain = "BotDetection:Domain";
    public const string Host = "BotDetection:Host";
    public const string RequestScope = "BotDetection:RequestScope";

    /// <summary>
    ///     Cached <see cref="SiteProfiles.EffectiveThresholds"/> for the current
    ///     request, stamped by
    ///     <see cref="SiteProfiles.IEffectivePolicyResolver.ResolveThresholds"/>.
    /// </summary>
    public const string EffectiveThresholds = "BotDetection:EffectiveThresholds";

    /// <summary>
    ///     Request-scope flag set by <see cref="DomainNormalizer.Resolve(HttpContext)"/> when the
    ///     gateway evaluated the TLS SNI for this connection but it was NOT served (no cert covers
    ///     it: default-cert fallback, a no-SNI / bare-IP handshake, or a spoofed domain). A detector
    ///     atom surfaces this as the <c>tls.sni_not_served</c> signal — the implausible-domain-at-TLS
    ///     tell. Absent on non-gateway topologies (no SNI evaluation) and on served connections.
    /// </summary>
    public const string TlsSniNotServed = "BotDetection:TlsSniNotServed";
}

/// <summary>
///     Connection-scope item keys written by the TLS-edge gateway's cert-selector (on
///     <c>IConnectionItemsFeature.Items</c>) and read per-request by <see cref="DomainNormalizer"/>.
///     The gateway is the only place the client's real ClientHello SNI is visible, and it only
///     completes the handshake for domains it holds a cert for — so a validated SNI is a trusted
///     domain source in a way the post-TLS client-supplied Host header is not.
/// </summary>
public static class TlsConnectionKeys
{
    /// <summary>
    ///     The normalized SNI host the gateway served a matching cert for on this connection
    ///     (present only when a SAN covered the SNI — the trust anchor). Absent when not served.
    /// </summary>
    public const string ValidatedSni = "sb.tls.validated_sni";

    /// <summary>
    ///     Set to <c>true</c> by the gateway cert-selector for every TLS connection it evaluates,
    ///     served or not — so the core can distinguish a gateway-seen-but-not-served connection
    ///     (resolve to "unknown" + signal) from a non-gateway topology (fall back to the Host header).
    /// </summary>
    public const string SniEvaluated = "sb.tls.sni_evaluated";
}