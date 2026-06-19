using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Proxy;

/// <inheritdoc />
public sealed class TransportHeaderTrust : ITransportHeaderTrust
{
    private readonly IOptions<BotDetectionOptions> _options;

    public TransportHeaderTrust(IOptions<BotDetectionOptions> options)
    {
        _options = options;
    }

    public TransportTrustResult Evaluate(BlackboardState state)
    {
        var result = Decide(state.HttpContext);
        state.WriteSignal(SignalKeys.TransportHeadersTrusted, result.Trusted);
        state.WriteSignal(SignalKeys.TransportTrustReason, result.Reason);
        return result;
    }

    /// <summary>
    ///     Context item key the BDF replay endpoint sets on its synthetic HttpContexts
    ///     to opt them into header trust. RFC 5737 TEST-NET ranges (192.0.2.0/24,
    ///     198.51.100.0/24, 203.0.113.0/24) are used per-scenario for reputation
    ///     isolation but read as untrusted public peers under the Auto fallthrough,
    ///     which would skip every header-forwarded TLS / JA3 / JA4 signal on every
    ///     BDF replay. This key is intended ONLY for the synthetic test path -- the
    ///     BdfReplay endpoint is itself gated by api-key + rate limit, so a hostile
    ///     real request cannot reach this codepath. Production peer trust still flows
    ///     through TrustedProxyIps / Auto-private-peer logic below.
    /// </summary>
    public const string SyntheticTrustOverrideKey = "StyloBot.BdfReplay.TrustHeaders";

    /// <summary>Pure decision logic (no signal writes), exposed for testing.</summary>
    public TransportTrustResult Decide(HttpContext ctx)
    {
        var opts = _options.Value.TransportTrust;
        if (opts.Mode == TransportTrustMode.Off)
            return new TransportTrustResult(true, "GateOff");

        // BDF replay synthetic override -- see field doc above.
        if (ctx.Items.TryGetValue(SyntheticTrustOverrideKey, out var marker)
            && marker is bool b && b)
            return new TransportTrustResult(true, "BdfReplaySynthetic");

        var peer = ctx.Connection?.RemoteIpAddress;

        // Dual-stack Kestrel can present an IPv4 peer as an IPv4-mapped IPv6 address
        // (::ffff:a.b.c.d). Unmap it so IPv4 CIDR allowlist entries and the
        // loopback/private check compare like-for-like (CidrHelper requires matching
        // address families), otherwise a trusted IPv4 proxy would read as untrusted.
        if (peer is not null && peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        // Allowlist applies in both Auto and Strict.
        if (peer is not null && opts.TrustedProxyIps.Count > 0)
        {
            foreach (var entry in opts.TrustedProxyIps)
            {
                var cidr = entry;
                if (!cidr.Contains('/') && System.Net.IPAddress.TryParse(cidr, out var single))
                    cidr = single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                        ? cidr + "/128"
                        : cidr + "/32";
                if (CidrHelper.IsInSubnet(peer, cidr))
                    return new TransportTrustResult(true, "AllowlistedPeer");
            }
        }

        if (opts.Mode == TransportTrustMode.Strict)
            return new TransportTrustResult(false, "NotAllowlisted");

        // Auto: loopback / private peer (the loopback-fronted production topology).
        if (opts.TrustPrivatePeers && NetworkHelper.IsLocalIp(peer))
            return new TransportTrustResult(true, "PrivatePeer");

        // Auto fallthrough: public peers are never trusted by header inspection alone.
        // Public-IP edges (Cloudflare, AWS ALB, etc.) MUST be added to TrustedProxyIps.
        return new TransportTrustResult(false, "UntrustedPublicPeer");
    }
}
