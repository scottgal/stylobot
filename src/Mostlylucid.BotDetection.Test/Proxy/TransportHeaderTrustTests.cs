using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Proxy;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Proxy;

public class TransportHeaderTrustTests
{
    private static TransportHeaderTrust Build(TransportTrustOptions opts)
    {
        var options = Options.Create(new BotDetectionOptions { TransportTrust = opts });
        return new TransportHeaderTrust(options);
    }

    private static HttpContext Ctx(string peerIp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
        return ctx;
    }

    [Fact]
    public void Off_mode_trusts_any_peer()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Off });
        var r = sut.Decide(Ctx("203.0.113.9"));
        Assert.True(r.Trusted);
        Assert.Equal("GateOff", r.Reason);
    }

    [Fact]
    public void Auto_trusts_loopback_peer()
    {
        var sut = Build(new TransportTrustOptions());
        Assert.True(sut.Decide(Ctx("127.0.0.1")).Trusted);
    }

    [Fact]
    public void Auto_trusts_private_peer()
    {
        var sut = Build(new TransportTrustOptions());
        Assert.Equal("PrivatePeer", sut.Decide(Ctx("10.0.0.5")).Reason);
    }

    [Fact]
    public void Auto_distrusts_public_direct_peer()
    {
        var sut = Build(new TransportTrustOptions());
        var r = sut.Decide(Ctx("203.0.113.9"));
        Assert.False(r.Trusted);
        Assert.Equal("UntrustedPublicPeer", r.Reason);
    }

    [Fact]
    public void Auto_public_peer_with_forwarded_header_is_not_trusted()
    {
        var sut = Build(new TransportTrustOptions());
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4";
        ctx.Request.Headers["CF-Connecting-IP"] = "1.2.3.4";
        var r = sut.Decide(ctx);
        Assert.False(r.Trusted);
        Assert.Equal("UntrustedPublicPeer", r.Reason);
    }

    [Fact]
    public void Allowlisted_public_peer_is_trusted()
    {
        var sut = Build(new TransportTrustOptions { TrustedProxyIps = ["203.0.113.0/24"] });
        Assert.Equal("AllowlistedPeer", sut.Decide(Ctx("203.0.113.9")).Reason);
    }

    [Fact]
    public void Strict_mode_distrusts_private_peer_without_allowlist()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Strict });
        var r = sut.Decide(Ctx("10.0.0.5"));
        Assert.False(r.Trusted);
        Assert.Equal("NotAllowlisted", r.Reason);
    }

    [Fact]
    public void Allowlisted_bare_ip_is_trusted()
    {
        var sut = Build(new TransportTrustOptions { TrustedProxyIps = ["203.0.113.9"] });
        Assert.Equal("AllowlistedPeer", sut.Decide(Ctx("203.0.113.9")).Reason);
    }

    [Fact]
    public void Auto_trusts_ipv6_loopback_peer()
    {
        var sut = Build(new TransportTrustOptions());
        Assert.True(sut.Decide(Ctx("::1")).Trusted);
    }

    [Fact]
    public void Auto_distrusts_null_peer()
    {
        var sut = Build(new TransportTrustOptions());
        var r = sut.Decide(new DefaultHttpContext()); // RemoteIpAddress is null
        Assert.False(r.Trusted);
    }

    [Fact]
    public void Auto_with_trust_private_peers_disabled_distrusts_private_peer()
    {
        var sut = Build(new TransportTrustOptions { TrustPrivatePeers = false });
        Assert.False(sut.Decide(Ctx("10.0.0.5")).Trusted);
    }

    [Fact]
    public void Strict_mode_trusts_allowlisted_peer()
    {
        var sut = Build(new TransportTrustOptions { Mode = TransportTrustMode.Strict, TrustedProxyIps = ["10.0.0.0/8"] });
        Assert.True(sut.Decide(Ctx("10.0.0.5")).Trusted);
    }
}
