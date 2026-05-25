using System.Net;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.RateLimiting;
using Xunit;

namespace Mostlylucid.BotDetection.Test.RateLimiting;

public class RateLimitSubjectResolverTests
{
    private static HttpContext CtxWithIp(IPAddress? ip)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = ip;
        return ctx;
    }

    [Fact]
    public void Resolves_all_seven_kinds_when_inputs_present()
    {
        var resolver = new RateLimitSubjectResolver(
            labels: new StaticLabelLookup("3b8XTkBR", "partner-feed"),
            regionMap: new StaticRegionMap("GB", "EU"));
        var ctx = CtxWithIp(IPAddress.Parse("203.0.113.5"));
        var detection = new DetectionFacts(
            PrimarySignature: "3b8XTkBR",
            BotType: "Scraper",
            BotName: "Acme/1.0",
            CountryCode: "GB");

        var subjects = resolver.Resolve(ctx, detection);
        var kinds = subjects.Select(s => s.Kind).ToHashSet();

        Assert.Contains(RateLimitSubjectKind.Fingerprint, kinds);
        Assert.Contains(RateLimitSubjectKind.BotType, kinds);
        Assert.Contains(RateLimitSubjectKind.BotName, kinds);
        Assert.Contains(RateLimitSubjectKind.Country, kinds);
        Assert.Contains(RateLimitSubjectKind.Region, kinds);
        Assert.Contains(RateLimitSubjectKind.PinnedLabel, kinds);
        Assert.Contains(RateLimitSubjectKind.IpSubnet, kinds);
    }

    [Fact]
    public void Empty_fields_skip_their_subject()
    {
        var resolver = new RateLimitSubjectResolver();
        var ctx = CtxWithIp(null);
        var detection = new DetectionFacts(
            PrimarySignature: "sig123",
            BotType: null,
            BotName: null,
            CountryCode: null);

        var subjects = resolver.Resolve(ctx, detection);
        var kinds = subjects.Select(s => s.Kind).ToHashSet();

        Assert.Contains(RateLimitSubjectKind.Fingerprint, kinds);
        Assert.DoesNotContain(RateLimitSubjectKind.BotType, kinds);
        Assert.DoesNotContain(RateLimitSubjectKind.BotName, kinds);
        Assert.DoesNotContain(RateLimitSubjectKind.Country, kinds);
        Assert.DoesNotContain(RateLimitSubjectKind.Region, kinds);
        Assert.DoesNotContain(RateLimitSubjectKind.IpSubnet, kinds);
    }

    [Fact]
    public void PinnedLabel_is_skipped_when_no_lookup_registered()
    {
        var resolver = new RateLimitSubjectResolver(labels: null, regionMap: null);
        var ctx = CtxWithIp(IPAddress.Parse("10.0.0.1"));
        var detection = new DetectionFacts("sig", "AI", null, "US");

        var subjects = resolver.Resolve(ctx, detection);
        Assert.DoesNotContain(subjects, s => s.Kind == RateLimitSubjectKind.PinnedLabel);
        // Country should still resolve; Region requires a map and is absent.
        Assert.Contains(subjects, s => s.Kind == RateLimitSubjectKind.Country && s.Value == "US");
        Assert.DoesNotContain(subjects, s => s.Kind == RateLimitSubjectKind.Region);
    }

    [Fact]
    public void Same_subnet_produces_same_key()
    {
        var resolver = new RateLimitSubjectResolver();
        var subA = resolver.Resolve(CtxWithIp(IPAddress.Parse("203.0.113.5")),
            new DetectionFacts(null, null, null, null));
        var subB = resolver.Resolve(CtxWithIp(IPAddress.Parse("203.0.113.99")),
            new DetectionFacts(null, null, null, null));

        var keyA = subA.First(s => s.Kind == RateLimitSubjectKind.IpSubnet).Value;
        var keyB = subB.First(s => s.Kind == RateLimitSubjectKind.IpSubnet).Value;
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Different_subnets_produce_different_keys()
    {
        var resolver = new RateLimitSubjectResolver();
        var subA = resolver.Resolve(CtxWithIp(IPAddress.Parse("203.0.113.5")),
            new DetectionFacts(null, null, null, null));
        var subB = resolver.Resolve(CtxWithIp(IPAddress.Parse("198.51.100.5")),
            new DetectionFacts(null, null, null, null));

        var keyA = subA.First(s => s.Kind == RateLimitSubjectKind.IpSubnet).Value;
        var keyB = subB.First(s => s.Kind == RateLimitSubjectKind.IpSubnet).Value;
        Assert.NotEqual(keyA, keyB);
    }

    private sealed class StaticLabelLookup : IPinnedLabelLookup
    {
        private readonly string _sig, _label;
        public StaticLabelLookup(string sig, string label) { _sig = sig; _label = label; }
        public string? GetLabel(string primarySignature) => primarySignature == _sig ? _label : null;
    }

    private sealed class StaticRegionMap : IRegionMap
    {
        private readonly string _country, _region;
        public StaticRegionMap(string country, string region) { _country = country; _region = region; }
        public string? GetRegion(string countryCode) =>
            string.Equals(countryCode, _country, StringComparison.OrdinalIgnoreCase) ? _region : null;
    }
}
