using System.Reflection;
using Mostlylucid.BotDetection.Domains;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Domains;

public sealed class PublicSuffixListTests
{
    private static PublicSuffixList Load()
    {
        using var stream = typeof(PublicSuffixList).Assembly
            .GetManifestResourceStream("Mostlylucid.BotDetection.Domains.PublicSuffixList.dat")!;
        return PublicSuffixList.LoadFrom(stream);
    }

    [Theory]
    [InlineData("acme.com",           "com",     "acme.com")]
    [InlineData("www.acme.com",       "com",     "acme.com")]
    [InlineData("api.sub.acme.co.uk", "co.uk",   "acme.co.uk")]
    [InlineData("acme.co.uk",         "co.uk",   "acme.co.uk")]
    [InlineData("stylo.bot",          "bot",     "stylo.bot")]
    [InlineData("auth.stylo.bot",     "bot",     "stylo.bot")]
    public void Registrable_domain_matches_PSL_rules(string host, string expectedSuffix, string expectedRegistrable)
    {
        var psl = Load();
        Assert.Equal(expectedSuffix, psl.GetPublicSuffix(host));
        Assert.Equal(expectedRegistrable, psl.GetRegistrableDomain(host));
    }

    [Fact]
    public void Host_shorter_than_suffix_returns_input()
    {
        var psl = Load();
        Assert.Equal("com", psl.GetRegistrableDomain("com"));
    }
}