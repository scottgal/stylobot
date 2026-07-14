using System.Net;
using FluentAssertions;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Security;

/// <summary>
///     Negative security tests for the Internal enforcement carve-out. Internal (LAN -> logonly)
///     is an enforcement BYPASS, so the trust decision must key off the REAL TCP peer and nothing
///     an attacker can set. Before this, the classification read <c>ip.is_local</c>, which is
///     computed from the resolved client IP and can be X-Forwarded-For-derived -- so a caller
///     behind an edge could spoof <c>X-Forwarded-For: 10.0.0.5</c> into Internal.
///
///     <see cref="InternalTrustEvaluator"/> only ever sees the peer <see cref="IPAddress"/>, so the
///     spoof cannot even be expressed here; these tests pin the peer-only + config semantics (the
///     deny path the old code lacked a single test for).
/// </summary>
public class InternalTrustEvaluatorTests
{
    private static readonly InternalTrustOptions Default = new(); // Enabled = true, no ranges

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.0.9")]
    public void Loopback_and_rfc1918_peers_are_trusted_by_default(string ip)
        => InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse(ip), Default).Should().BeTrue();

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("8.8.8.8")]
    [InlineData("1.2.3.4")]
    public void Public_peers_are_NEVER_trusted(string ip)
        => InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse(ip), Default).Should().BeFalse(
            "a public peer must never get the Internal bypass -- this is the deny case the spoof relied on");

    [Fact]
    public void Null_peer_is_not_trusted()
        => InternalTrustEvaluator.IsTrustedInternalPeer(null, Default).Should().BeFalse();

    [Fact]
    public void Disabled_trusts_nothing_not_even_loopback()
        => InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Loopback, new InternalTrustOptions { Enabled = false })
            .Should().BeFalse("Enabled=false removes the Internal path entirely -- zero bypass surface");

    [Fact]
    public void TrustedRanges_is_THE_RULE_only_peers_in_range_qualify()
    {
        var opts = new InternalTrustOptions { TrustedRanges = { "10.8.0.0/24" } };

        InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse("10.8.0.5"), opts).Should().BeTrue();
        InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse("192.168.1.10"), opts).Should().BeFalse(
            "explicit TrustedRanges is the rule; a private IP OUTSIDE the range is not Internal");
        InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Loopback, opts).Should().BeFalse(
            "once ranges are defined, loopback is not Internal unless it is listed");
    }

    [Fact]
    public void Bare_ip_in_TrustedRanges_matches_as_a_single_host()
    {
        var opts = new InternalTrustOptions { TrustedRanges = { "203.0.113.9" } };

        InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse("203.0.113.9"), opts).Should().BeTrue(
            "a bare IP is treated as /32 -- lets an operator trust one specific edge/proxy peer");
        InternalTrustEvaluator.IsTrustedInternalPeer(IPAddress.Parse("203.0.113.10"), opts).Should().BeFalse();
    }

    [Fact]
    public void IPv4_mapped_IPv6_peer_is_unmapped_before_range_match()
    {
        var opts = new InternalTrustOptions { TrustedRanges = { "10.8.0.0/24" } };
        var mapped = IPAddress.Parse("10.8.0.5").MapToIPv6(); // ::ffff:10.8.0.5, as dual-stack Kestrel presents it

        InternalTrustEvaluator.IsTrustedInternalPeer(mapped, opts).Should().BeTrue(
            "dual-stack Kestrel presents IPv4 peers as ::ffff:a.b.c.d; must unmap for the IPv4 CIDR compare");
    }
}
