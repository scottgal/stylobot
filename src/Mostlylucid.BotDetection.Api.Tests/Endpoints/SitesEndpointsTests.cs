using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Api.Tests.Endpoints;

public class SitesEndpointsTests
{
    [Fact]
    public void SiteDomainDto_FromRule_ExactHost_IsNotWildcard()
    {
        var rule = new SiteMapRule { Host = "blog.example.com", Profile = "generic" };

        var dto = SiteDomainDto.FromRule(rule);

        Assert.Equal("blog.example.com", dto.Host);
        Assert.Equal("generic", dto.Profile);
        Assert.Null(dto.Domain);
        Assert.False(dto.IsWildcard);
    }

    [Theory]
    [InlineData("*.staging.example.com")]
    [InlineData("api.*.example.com")]
    public void SiteDomainDto_FromRule_WildcardHost_IsWildcard(string host)
    {
        var rule = new SiteMapRule { Host = host, Profile = "staging", Domain = "example.com" };

        var dto = SiteDomainDto.FromRule(rule);

        Assert.Equal("example.com", dto.Domain);
        Assert.True(dto.IsWildcard);
    }

    [Fact]
    public void SiteDomainDto_FromRule_PreservesOptionalDomain()
    {
        var rule = new SiteMapRule { Host = "blog.example.com", Profile = "generic", Domain = "example.com" };

        var dto = SiteDomainDto.FromRule(rule);

        Assert.Equal("example.com", dto.Domain);
    }
}
