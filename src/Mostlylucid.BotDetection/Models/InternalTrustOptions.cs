using System.Net;
using System.Net.Sockets;
using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Controls the <see cref="BotType.Internal"/> classification -- the LAN-traffic carve-out
///     that routes a request to <c>logonly</c> instead of throttling/blocking it (admin curl,
///     dashboard self-poll, sidecar loopback, the BDF runner).
///
///     <para>
///         Because Internal is an <em>enforcement bypass</em>, the trust decision MUST be made
///         against the real TCP peer (<c>HttpContext.Connection.RemoteIpAddress</c>) and NEVER
///         against <c>X-Forwarded-For</c> or any other request header. A header-derived local-IP
///         check let an external caller behind an edge spoof <c>X-Forwarded-For: 10.0.0.5</c> and
///         be classified Internal -- a total detection bypass with one header. All evaluation here
///         is peer-only.
///     </para>
/// </summary>
public sealed class InternalTrustOptions
{
    /// <summary>
    ///     Master switch for the Internal path. When <c>false</c>, no request is ever classified
    ///     Internal, so there is no bypass surface at all (correct for a bare gateway with no
    ///     dashboard / sidecar / admin endpoints). Default <c>true</c> preserves the historical
    ///     behaviour -- now peer-verified, so it cannot be spoofed.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     When non-empty, this is THE RULE: a request is Internal ONLY if the real peer is in one
    ///     of these CIDR ranges (or a bare IP, treated as /32 or /128). The loopback/RFC1918
    ///     heuristic is disabled the moment you are explicit. When empty, the peer-based
    ///     loopback/RFC1918 heuristic in <see cref="InternalTrustEvaluator"/> applies. Evaluated
    ///     against the TCP peer only, never a forwarded header.
    /// </summary>
    public List<string> TrustedRanges { get; set; } = [];
}

/// <summary>
///     Peer-only evaluation of <see cref="InternalTrustOptions"/>. Static + pure so it is trivially
///     unit-testable (including the negative/spoof cases) without a DI container.
/// </summary>
public static class InternalTrustEvaluator
{
    /// <summary>
    ///     Returns true only when <paramref name="peer"/> (the real TCP peer) is trusted for the
    ///     Internal bypass under <paramref name="options"/>. Never consults any request header.
    /// </summary>
    public static bool IsTrustedInternalPeer(IPAddress? peer, InternalTrustOptions? options)
    {
        if (options is null || !options.Enabled) return false;
        if (peer is null) return false;

        // Dual-stack Kestrel can present IPv4 peers as ::ffff:a.b.c.d; unmap so IPv4 CIDR entries
        // compare like-for-like (mirrors IEndpointPolicyResolver.IsLocalOrTrustedCaller).
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();

        // Explicit ranges are authoritative: peer must be in one of them, full stop.
        if (options.TrustedRanges.Count > 0)
        {
            foreach (var entry in options.TrustedRanges)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var cidr = entry.Trim();
                if (!cidr.Contains('/') && IPAddress.TryParse(cidr, out var single))
                    cidr = single.AddressFamily == AddressFamily.InterNetworkV6 ? cidr + "/128" : cidr + "/32";
                if (CidrHelper.IsInSubnet(peer, cidr))
                    return true;
            }
            return false;
        }

        // No explicit ranges: peer-based loopback / RFC1918 / RFC4193 heuristic.
        return NetworkHelper.IsLocalIp(peer);
    }
}
