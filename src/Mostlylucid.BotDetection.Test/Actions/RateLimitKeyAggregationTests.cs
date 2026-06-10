using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Pins the subnet- and ASN-aggregation semantics of the
///     <see cref="RateLimitKey"/> enum. The existing
///     <see cref="RateLimitKey.Ip"/> mode buckets per individual IP, which
///     is useless against real botnets: 65,000 IPs in a /16 hit 65,000
///     separate buckets and each consumes its full burst.
///
///     Subnet mode auto-picks the prefix size by address family (/24 for
///     IPv4, /48 for IPv6 -- the same conventions HAProxy stick-tables
///     and Cloudflare WAF rate-limits use). ASN mode reads
///     <see cref="SignalKeys.IpAsn"/> from the aggregated evidence, which
///     <c>Mostlylucid.GeoDetection.Contributor</c> populates. The
///     composite <see cref="RateLimitKey.AsnAndSignature"/> mode is the
///     real-botnet-effective key: each ASN has its own budget while
///     fingerprint within the ASN is still discriminated.
/// </summary>
public class RateLimitKeyAggregationTests
{
    // === Subnet (auto-pick /24 IPv4 + /48 IPv6) ============================

    [Fact]
    public async Task Subnet_TwoIPv4InSame24_ShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Subnet,
            OverLimitAction = "throttle-status",
        });

        var first = await policy.ExecuteAsync(NewContext("203.0.113.5"), EmptyEvidence());
        Assert.True(first.Continue, "burst of 1 means the first request must allow");

        // Different host inside the same /24 must hit the SAME bucket and be denied.
        var second = await policy.ExecuteAsync(NewContext("203.0.113.99"), EmptyEvidence());
        Assert.False(second.Continue,
            "203.0.113.99 should share a bucket with 203.0.113.5 (same /24)");
    }

    [Fact]
    public async Task Subnet_TwoIPv4InDifferentSubnets_DoNotShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Subnet,
            OverLimitAction = "throttle-status",
        });

        var first = await policy.ExecuteAsync(NewContext("203.0.113.5"), EmptyEvidence());
        Assert.True(first.Continue);

        var different = await policy.ExecuteAsync(NewContext("203.0.114.5"), EmptyEvidence());
        Assert.True(different.Continue, "different /24s must bucket independently");
    }

    [Fact]
    public async Task Subnet_TwoIPv6InSame48_ShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Subnet,
            OverLimitAction = "throttle-status",
        });

        var first = await policy.ExecuteAsync(NewContext("2001:db8:1::1"), EmptyEvidence());
        Assert.True(first.Continue);

        var second = await policy.ExecuteAsync(NewContext("2001:db8:1::ff"), EmptyEvidence());
        Assert.False(second.Continue, "IPv6 addresses sharing /48 must share a bucket");
    }

    [Fact]
    public async Task Subnet_TwoIPv6InDifferent48_DoNotShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Subnet,
            OverLimitAction = "throttle-status",
        });

        await policy.ExecuteAsync(NewContext("2001:db8:1::1"), EmptyEvidence());
        var different = await policy.ExecuteAsync(NewContext("2001:db8:2::1"), EmptyEvidence());
        Assert.True(different.Continue, "different /48s must bucket independently");
    }

    // === Asn ================================================================

    [Fact]
    public async Task Asn_TwoIpsInSameAsn_ShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Asn,
            OverLimitAction = "throttle-status",
        });

        // Two different IPs, same ASN advertised by the geo contributor.
        var first = await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithAsn(12345));
        Assert.True(first.Continue);

        var second = await policy.ExecuteAsync(NewContext("198.51.100.7"), EvidenceWithAsn(12345));
        Assert.False(second.Continue,
            "different IPs in the same ASN must share the ASN-keyed bucket");
    }

    [Fact]
    public async Task Asn_DifferentAsns_DoNotShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Asn,
            OverLimitAction = "throttle-status",
        });

        await policy.ExecuteAsync(NewContext("203.0.113.5"), EvidenceWithAsn(12345));
        var different = await policy.ExecuteAsync(NewContext("198.51.100.7"), EvidenceWithAsn(99999));
        Assert.True(different.Continue, "different ASNs bucket independently");
    }

    [Fact]
    public async Task Asn_NoAsnSignal_AllowsRequest_FailOpen()
    {
        // When the geo contributor isn't wired and IpAsn is absent, the
        // policy should fail open rather than route every visitor through
        // a global bucket.
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1, KeyBy = RateLimitKey.Asn,
            OverLimitAction = "throttle-status",
        });

        for (var i = 0; i < 5; i++)
        {
            var result = await policy.ExecuteAsync(NewContext("203.0.113.5"), EmptyEvidence());
            Assert.True(result.Continue,
                $"request {i}: missing ASN must fail open, not bucket");
        }
    }

    // === AsnAndSignature (composite) =======================================

    [Fact]
    public async Task AsnAndSignature_SameAsnAndSig_ShareBucket()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1,
            KeyBy = RateLimitKey.AsnAndSignature,
            OverLimitAction = "throttle-status",
        });

        await policy.ExecuteAsync(
            NewContext("203.0.113.5"),
            EvidenceWith(asn: 12345, sig: "sig:abc"));
        var second = await policy.ExecuteAsync(
            NewContext("198.51.100.7"),
            EvidenceWith(asn: 12345, sig: "sig:abc"));

        Assert.False(second.Continue,
            "same ASN + same signature -> same composite bucket");
    }

    [Fact]
    public async Task AsnAndSignature_SameAsnDifferentSig_BucketIndependently()
    {
        var (policy, _) = Build(new RateLimitActionOptions
        {
            RequestsPerMinute = 60, BurstSize = 1,
            KeyBy = RateLimitKey.AsnAndSignature,
            OverLimitAction = "throttle-status",
        });

        await policy.ExecuteAsync(
            NewContext("203.0.113.5"),
            EvidenceWith(asn: 12345, sig: "sig:abc"));
        var different = await policy.ExecuteAsync(
            NewContext("198.51.100.7"),
            EvidenceWith(asn: 12345, sig: "sig:zzz"));

        Assert.True(different.Continue,
            "different signature within the same ASN must have its own bucket");
    }

    // === Helpers ===========================================================

    private static (RateLimitActionPolicy policy, ActionPolicyRegistry registry) Build(RateLimitActionOptions opts)
    {
        var registry = new ActionPolicyRegistry(
            Options.Create(new BotDetectionOptions()),
            Array.Empty<IActionPolicyFactory>(),
            Array.Empty<IActionPolicy>());
        var store = new InMemoryTokenBucketStore();
        var policy = new RateLimitActionPolicy("rate-limit-test", opts, store, registry);
        return (policy, registry);
    }

    private static HttpContext NewContext(string remoteIp)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        return ctx;
    }

    private static AggregatedEvidence EmptyEvidence() => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object>(),
    };

    private static AggregatedEvidence EvidenceWithAsn(int asn) => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object> { [SignalKeys.IpAsn] = asn },
    };

    private static AggregatedEvidence EvidenceWith(int asn, string sig) => new()
    {
        BotProbability = 0.5,
        Confidence = 0.8,
        RiskBand = RiskBand.Medium,
        Signals = new Dictionary<string, object>
        {
            [SignalKeys.IpAsn] = asn,
            [SignalKeys.PrimarySignature] = sig,
        },
    };
}
