using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.SiteProfiles;

namespace Mostlylucid.BotDetection.Test.SiteProfiles;

public class SiteProfileResolverTests
{
    private static SiteProfileResolver BuildResolver(params (string host, string profile)[] rules)
    {
        var catalog = new SiteProfileCatalog(NullLogger<SiteProfileCatalog>.Instance);
        var map = new SiteMapOptions
        {
            DefaultProfile = "generic",
            Domains = rules.Select(r => new SiteMapRule { Host = r.host, Profile = r.profile }).ToList()
        };
        return new SiteProfileResolver(
            catalog,
            Options.Create(map),
            NullLogger<SiteProfileResolver>.Instance);
    }

    [Fact]
    public void ExactHost_Wins()
    {
        var resolver = BuildResolver(
            ("blog.example.com", "wordpress"),
            ("*.example.com", "aspnet"));

        var profile = resolver.ResolveByHost("blog.example.com");
        Assert.Equal("wordpress", profile?.Id);
    }

    [Fact]
    public void LeadingWildcard_MatchesSubdomain()
    {
        var resolver = BuildResolver(("*.staging.example.com", "aspnet"));

        var profile = resolver.ResolveByHost("foo.staging.example.com");
        Assert.Equal("aspnet", profile?.Id);
    }

    [Fact]
    public void LeadingWildcard_DoesNotMatchBaseSuffix()
    {
        // *.staging.example.com requires AT LEAST one extra leading segment.
        var resolver = BuildResolver(("*.staging.example.com", "aspnet"));
        Assert.Equal("generic", resolver.ResolveByHost("staging.example.com")?.Id);
    }

    [Fact]
    public void LeadingWildcard_MatchesMultipleSubdomains()
    {
        var resolver = BuildResolver(("*.staging.example.com", "aspnet"));
        Assert.Equal("aspnet", resolver.ResolveByHost("api.test.staging.example.com")?.Id);
    }

    [Fact]
    public void MidWildcard_MatchesSingleSegment()
    {
        var resolver = BuildResolver(("api.*.example.com", "aspnet"));
        Assert.Equal("aspnet", resolver.ResolveByHost("api.eu.example.com")?.Id);
    }

    [Fact]
    public void MidWildcard_DoesNotMatchExtraSegments()
    {
        var resolver = BuildResolver(("api.*.example.com", "aspnet"));
        Assert.Equal("generic", resolver.ResolveByHost("api.eu.uk.example.com")?.Id);
    }

    [Fact]
    public void UnknownHost_FallsBackToDefault()
    {
        var resolver = BuildResolver(("blog.example.com", "wordpress"));
        Assert.Equal("generic", resolver.ResolveByHost("unknown.example.com")?.Id);
    }

    [Fact]
    public void PortIsStripped_BeforeMatch()
    {
        var resolver = BuildResolver(("blog.example.com", "wordpress"));
        Assert.Equal("wordpress", resolver.ResolveByHost("blog.example.com:8443")?.Id);
    }

    [Fact]
    public void CaseInsensitive()
    {
        var resolver = BuildResolver(("Blog.Example.com", "wordpress"));
        Assert.Equal("wordpress", resolver.ResolveByHost("BLOG.EXAMPLE.COM")?.Id);
    }

    [Fact]
    public void UnknownProfile_InRule_LogsAndSkips()
    {
        // Rule referencing a nonexistent profile is silently dropped.
        var resolver = BuildResolver(("blog.example.com", "nonexistent-profile"));
        Assert.Equal("generic", resolver.ResolveByHost("blog.example.com")?.Id);
    }

    [Fact]
    public void Resolve_FromHttpContext_CachesPerRequest()
    {
        var resolver = BuildResolver(("blog.example.com", "wordpress"));
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString("blog.example.com");

        var first = resolver.Resolve(ctx);
        var second = resolver.Resolve(ctx);

        Assert.Same(first, second);
        Assert.Equal("wordpress", first?.Id);
    }
}
