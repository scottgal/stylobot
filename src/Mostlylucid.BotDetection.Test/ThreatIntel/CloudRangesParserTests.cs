using Mostlylucid.BotDetection.ThreatIntel.Providers;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class CloudRangesParserTests
{
    [Fact]
    public void ParseAwsJson_ExtractsV4AndV6Prefixes()
    {
        const string body = """
            {
              "syncToken": "0",
              "createDate": "2026-05-18-00-00-00",
              "prefixes": [
                { "ip_prefix": "13.34.0.0/15", "region": "us-east-1", "service": "AMAZON" },
                { "ip_prefix": "52.0.0.0/15",  "region": "us-east-1", "service": "EC2"    }
              ],
              "ipv6_prefixes": [
                { "ipv6_prefix": "2600:1f00::/24", "region": "us-east-1", "service": "AMAZON" }
              ]
            }
            """;
        var cidrs = CloudRangesProvider.ParseAwsJson(body).ToList();
        Assert.Equal(new[] { "13.34.0.0/15", "52.0.0.0/15", "2600:1f00::/24" }, cidrs);
    }

    [Fact]
    public void ParseGcpJson_ExtractsBothIpv4AndIpv6FromEachPrefixEntry()
    {
        const string body = """
            {
              "syncToken": "1",
              "creationTime": "2026-05-18T00:00:00",
              "prefixes": [
                { "ipv4Prefix": "34.0.0.0/15", "service": "Google Cloud", "scope": "us-east1" },
                { "ipv6Prefix": "2001:4860::/32", "service": "Google Cloud" }
              ]
            }
            """;
        var cidrs = CloudRangesProvider.ParseGcpJson(body).ToList();
        Assert.Equal(new[] { "34.0.0.0/15", "2001:4860::/32" }, cidrs);
    }

    [Fact]
    public void ParseAzureJson_FlattensServiceTagPrefixes()
    {
        const string body = """
            {
              "changeNumber": 1,
              "cloud": "Public",
              "values": [
                {
                  "name": "AzureCloud.eastus",
                  "id": "AzureCloud.eastus",
                  "properties": {
                    "addressPrefixes": ["20.0.0.0/12", "2603:1000::/24"]
                  }
                },
                {
                  "name": "Storage.eastus",
                  "id": "Storage.eastus",
                  "properties": {
                    "addressPrefixes": ["52.0.0.0/14"]
                  }
                }
              ]
            }
            """;
        var cidrs = CloudRangesProvider.ParseAzureJson(body).ToList();
        Assert.Equal(new[] { "20.0.0.0/12", "2603:1000::/24", "52.0.0.0/14" }, cidrs);
    }

    [Fact]
    public void ParseFastlyJson_ExtractsBothAddressArrays()
    {
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
    public void ParseCidrText_StripsCommentsAndBlankLines()
    {
        const string body = """
            # Cloudflare IPv4 ranges
            173.245.48.0/20

            103.21.244.0/22
            # end
            """;
        var cidrs = CloudRangesProvider.ParseCidrText(body).ToList();
        Assert.Equal(new[] { "173.245.48.0/20", "103.21.244.0/22" }, cidrs);
    }

    [Fact]
    public void ParseByFormat_UnknownFormatThrows()
    {
        Assert.Throws<NotSupportedException>(() => CloudRangesProvider.ParseByFormat("oracle-json", "{}").ToList());
    }
}
