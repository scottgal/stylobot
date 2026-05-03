using Mostlylucid.BotDetection.Licensing;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Licensing;

public sealed class CloudPoolHostsTests
{
    [Theory]
    [InlineData("acme.azurewebsites.net", true)]
    [InlineData("my-app.vercel.app", true)]
    [InlineData("something.herokuapp.com", true)]
    [InlineData("app.fly.dev", true)]
    [InlineData("project.netlify.app", true)]
    [InlineData("thing.up.railway.app", true)]
    [InlineData("api.run.app", true)]
    [InlineData("page.github.io", true)]
    [InlineData("site.pages.dev", true)]
    [InlineData("acme.com", false)]
    [InlineData("example.org", false)]
    [InlineData("subdomain.mysite.com", false)]
    [InlineData("localhost", false)]
    [InlineData("", false)]
    public void IsCloudPoolHost_ClassifiesCorrectly(string host, bool expected)
    {
        Assert.Equal(expected, CloudPoolHosts.IsCloudPoolHost(host));
    }

    [Theory]
    [InlineData("ACME.COM", "acme.com")]
    [InlineData("acme.com:8080", "acme.com")]
    [InlineData("  Acme.COM  ", "acme.com")]
    [InlineData("api.acme.com:443", "api.acme.com")]
    public void NormalizeHost_LowercasesAndStripsPort(string input, string expected)
    {
        Assert.Equal(expected, CloudPoolHosts.NormalizeHost(input));
    }

    [Fact]
    public void ExactSuffixMatchAtApex_CountsAsPool()
    {
        // If someone licensed "vercel.app" at the apex, that IS the cloud-pool host.
        Assert.True(CloudPoolHosts.IsCloudPoolHost("foo.vercel.app"));
    }

    [Fact]
    public void PartialMatchIsNotPool()
    {
        // "fake-vercel.app" should NOT match "vercel.app" (that's a different domain).
        Assert.False(CloudPoolHosts.IsCloudPoolHost("fake-vercel.app"));
    }
}
