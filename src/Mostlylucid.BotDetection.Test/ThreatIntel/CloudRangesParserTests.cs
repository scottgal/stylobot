using Mostlylucid.BotDetection.ThreatIntel.Providers;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class CloudRangesParserTests
{
    [Fact]
    public void ParseFastlyJson_ExtractsBothAddressArrays()
    {
        // Fastly's public-ip-list endpoint - the one cloud vendor BotListFetcher
        // doesn't cover, so CloudRangesProvider fetches + parses it directly.
        // AWS / GCP / Azure / Cloudflare parsers live in BotListFetcher and are
        // exercised by its own tests; CloudRangesProvider just consumes the
        // pre-parsed per-vendor dict via GetDatacenterIpRangesByVendorAsync.
        const string body = """
            {
              "addresses": ["23.235.32.0/20", "151.101.0.0/16"],
              "ipv6_addresses": ["2a04:4e40::/32"]
            }
            """;
        var cidrs = CloudRangesProvider.ParseFastlyJson(body).ToList();
        Assert.Equal(new[] { "23.235.32.0/20", "151.101.0.0/16", "2a04:4e40::/32" }, cidrs);
    }

    [Fact]
    public void ParseFastlyJson_EmptyArrays_NoEntries()
    {
        Assert.Empty(CloudRangesProvider.ParseFastlyJson("""{"addresses":[],"ipv6_addresses":[]}"""));
    }
}
