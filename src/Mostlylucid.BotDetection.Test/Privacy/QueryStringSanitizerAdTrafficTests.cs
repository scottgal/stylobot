using System.Security.Cryptography;
using Mostlylucid.BotDetection.Privacy;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Privacy;

public class QueryStringSanitizerAdTrafficTests
{
    private static readonly byte[] TestKey = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void DetectAdTrafficParams_NoQueryString_ReturnsEmpty()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(null, null, TestKey);
        Assert.False(result.UtmPresent);
        Assert.Equal("organic", result.SourcePlatform);
        Assert.False(result.HasGclid);
    }

    [Fact]
    public void DetectAdTrafficParams_Gclid_DetectsGooglePlatform()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?gclid=Cj0KCQiAqsitBhDlARIsAGMR1Rjabc123", null, TestKey);
        Assert.True(result.UtmPresent);
        Assert.True(result.HasGclid);
        Assert.Equal("google", result.SourcePlatform);
        Assert.NotNull(result.ClickIdHash);
        Assert.DoesNotContain("Cj0KCQiAqsitBhDlARIsAGMR1Rjabc123", result.ClickIdHash!);
    }

    [Fact]
    public void DetectAdTrafficParams_Fbclid_DetectsMetaPlatform()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?fbclid=AbC123xyz", null, TestKey);
        Assert.True(result.UtmPresent);
        Assert.True(result.HasFbclid);
        Assert.Equal("meta", result.SourcePlatform);
    }

    [Fact]
    public void DetectAdTrafficParams_UtmSource_HashesValue()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?utm_source=google&utm_campaign=spring_sale&utm_medium=cpc", null, TestKey);
        Assert.True(result.UtmPresent);
        Assert.NotNull(result.SourceHash);
        Assert.NotNull(result.CampaignHash);
        Assert.NotNull(result.MediumHash);
        Assert.NotEqual(result.SourceHash, result.CampaignHash);
        Assert.DoesNotContain("google", result.SourceHash!);
    }

    [Fact]
    public void DetectAdTrafficParams_HashesAreDeterministic()
    {
        const string qs = "?utm_campaign=test_campaign";
        var r1 = QueryStringSanitizer.DetectAdTrafficParams(qs, null, TestKey);
        var r2 = QueryStringSanitizer.DetectAdTrafficParams(qs, null, TestKey);
        Assert.Equal(r1.CampaignHash, r2.CampaignHash);
    }

    [Fact]
    public void DetectAdTrafficParams_UtmMediumOnly_SetsUtmPresent()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?utm_medium=cpc", null, TestKey);
        Assert.True(result.UtmPresent);
        Assert.NotEqual("organic", result.SourcePlatform);
    }

    [Fact]
    public void DetectAdTrafficParams_MalformedPercentEncoding_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            QueryStringSanitizer.DetectAdTrafficParams("?utm_source=google&bad=%C0%80%FE", null, TestKey));
        Assert.Null(ex);
    }

    [Fact]
    public void DetectAdTrafficParams_ReferrerMismatch_GclidNoReferer()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?gclid=abc123", referer: null, hmacKey: TestKey);
        Assert.True(result.ReferrerMismatch);
        Assert.False(result.ReferrerPresent);
    }

    [Fact]
    public void DetectAdTrafficParams_NoMismatch_GclidWithGoogleReferer()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?gclid=abc123",
            referer: "https://www.google.com/aclk?sa=l",
            hmacKey: TestKey);
        Assert.False(result.ReferrerMismatch);
        Assert.True(result.ReferrerPresent);
    }

    [Fact]
    public void DetectAdTrafficParams_Mismatch_GclidWithWrongReferer()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?gclid=abc123",
            referer: "https://www.someblogsite.com",
            hmacKey: TestKey);
        Assert.True(result.ReferrerMismatch);
    }

    [Fact]
    public void DetectAdTrafficParams_NoParams_ReturnsOrganic()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?page=2&sort=desc", null, TestKey);
        Assert.False(result.UtmPresent);
        Assert.Equal("organic", result.SourcePlatform);
    }

    [Fact]
    public void DetectAdTrafficParams_NullKey_StillHashesWithSha256()
    {
        var result = QueryStringSanitizer.DetectAdTrafficParams(
            "?utm_source=google", null, null);
        Assert.True(result.UtmPresent);
        Assert.NotNull(result.SourceHash);
    }
}
