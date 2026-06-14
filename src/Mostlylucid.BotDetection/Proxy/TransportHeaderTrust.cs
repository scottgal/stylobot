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

    /// <summary>Pure decision logic (no signal writes), exposed for testing.</summary>
    public TransportTrustResult Decide(HttpContext ctx)
    {
        var opts = _options.Value.TransportTrust;
        if (opts.Mode == TransportTrustMode.Off)
            return new TransportTrustResult(true, "GateOff");

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
