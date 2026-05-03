using Mostlylucid.BotDetection.Licensing;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Licensing;

public sealed class DomainEntitlementValidatorTests
{
    [Fact]
    public void NoDomains_IsPassThrough()
    {
        var v = new DomainEntitlementValidator(null);
        Assert.False(v.IsEnforcing);
        Assert.Equal(DomainEntitlementResult.NotEnforced, v.Check("totally-random.example"));
    }

    [Theory]
    [InlineData("acme.com", "acme.com")]                   // exact licensed domain
    [InlineData("acme.com", "www.acme.com")]               // subdomain
    [InlineData("acme.com", "api.acme.com")]
    [InlineData("acme.com", "deep.nested.acme.com")]
    [InlineData("acme.com", "ACME.COM")]                   // case-insensitive
    [InlineData("acme.com", "api.acme.com:8080")]          // port stripped
    public void LicensedDomain_MatchesSubdomains(string licensed, string requestHost)
    {
        var v = new DomainEntitlementValidator(new[] { licensed });
        Assert.Equal(DomainEntitlementResult.Licensed, v.Check(requestHost));
    }

    [Theory]
    [InlineData("acme.com", "example.com")]                // different TLD+1
    [InlineData("acme.com", "acmecorp.com")]               // prefix collision must NOT match
    [InlineData("acme.com", "evil-acme.com")]
    public void LicensedDomain_DoesNotMatchUnrelated(string licensed, string requestHost)
    {
        var v = new DomainEntitlementValidator(new[] { licensed });
        Assert.Equal(DomainEntitlementResult.Mismatch, v.Check(requestHost));
    }

    [Fact]
    public void ExactEntry_DoesNotMatchSubdomains()
    {
        var v = new DomainEntitlementValidator(new[] { "=admin.acme.com" });
        Assert.Equal(DomainEntitlementResult.Licensed, v.Check("admin.acme.com"));
        Assert.Equal(DomainEntitlementResult.Mismatch, v.Check("foo.admin.acme.com"));
        Assert.Equal(DomainEntitlementResult.Mismatch, v.Check("acme.com"));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("127.5.10.1")]
    [InlineData("api.localhost")]
    [InlineData("app.local")]
    [InlineData("myapp.test")]
    [InlineData("foo.localdomain")]
    [InlineData("mysvc.internal")]
    public void DevHosts_AlwaysAllowed(string devHost)
    {
        var v = new DomainEntitlementValidator(new[] { "acme.com" });
        Assert.Equal(DomainEntitlementResult.Licensed, v.Check(devHost));
    }

    [Theory]
    [InlineData("acme.azurewebsites.net")]
    [InlineData("api-acme.vercel.app")]
    [InlineData("staging.herokuapp.com")]
    [InlineData("thing.up.railway.app")]
    [InlineData("mything.run.app")]
    public void CloudPoolHosts_ClassifyAsPoolMismatch(string cloudHost)
    {
        var v = new DomainEntitlementValidator(new[] { "acme.com" });
        Assert.Equal(DomainEntitlementResult.MismatchCloudPool, v.Check(cloudHost));
    }

    [Fact]
    public void Statistics_IncludeTopUnfamiliarHosts()
    {
        var v = new DomainEntitlementValidator(new[] { "acme.com" });
        v.Check("acme.com");
        v.Check("api.acme.com");
        for (var i = 0; i < 3; i++) v.Check("rogue.example.com");
        v.Check("also-weird.example.org");

        var stats = v.GetStatistics();
        Assert.True(stats.IsEnforcing);
        Assert.Equal(2, stats.RequestsLicensed);
        Assert.Equal(4, stats.RequestsMismatched);
        Assert.True(stats.MismatchRatio > 0.6 && stats.MismatchRatio < 0.7);
        Assert.Contains(stats.TopUnfamiliarHosts, h => h.Host == "rogue.example.com" && h.Hits == 3);
    }

    [Fact]
    public void MixedLicense_CombinesWildcardAndExact()
    {
        var v = new DomainEntitlementValidator(new[] { "acme.com", "=partner.example.org" });
        Assert.Equal(DomainEntitlementResult.Licensed, v.Check("api.acme.com"));
        Assert.Equal(DomainEntitlementResult.Licensed, v.Check("partner.example.org"));
        Assert.Equal(DomainEntitlementResult.Mismatch, v.Check("other.example.org"));
        Assert.Equal(DomainEntitlementResult.Mismatch, v.Check("foo.partner.example.org"));
    }

    [Fact]
    public void EmptyHost_ReturnsNoHost()
    {
        var v = new DomainEntitlementValidator(new[] { "acme.com" });
        Assert.Equal(DomainEntitlementResult.NoHost, v.Check(""));
        Assert.Equal(DomainEntitlementResult.NoHost, v.Check(null));
    }
}
