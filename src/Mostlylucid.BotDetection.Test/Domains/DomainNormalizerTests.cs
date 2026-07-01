using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Domains;

public sealed class DomainNormalizerTests
{
    private static DomainNormalizer New(DomainNormalizerOptions? opts = null)
    {
        opts ??= new DomainNormalizerOptions();
        return new DomainNormalizer(Options.Create(opts), PublicSuffixList.LoadEmbedded());
    }

    [Theory]
    [InlineData("acme.com",         "acme.com")]
    [InlineData("Www.Acme.COM",     "acme.com")]
    [InlineData("acme.com:8080",    "acme.com")]
    [InlineData("auth.stylo.bot",   "stylo.bot")]
    [InlineData("www.acme.co.uk",   "acme.co.uk")]
    [InlineData("api.sub.acme.co.uk", "acme.co.uk")]
    public void Normalizes_to_registrable(string host, string expected)
    {
        var n = New();
        Assert.Equal(expected, n.NormalizeDomain(host));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.0.42")]
    [InlineData("172.16.5.5")]
    public void RFC1918_and_localhost_return_LocalTag(string host)
    {
        var n = New();
        Assert.Equal("local", n.NormalizeDomain(host));
        Assert.Equal(host.ToLowerInvariant(), n.NormalizeHost(host));
    }

    [Theory]
    [InlineData("myapp.azurewebsites.net", "myapp.azurewebsites.net")]
    [InlineData("shop.vercel.app",         "shop.vercel.app")]
    public void HostingProviderException_treats_full_label_as_registrable(string host, string expected)
    {
        var n = New();
        Assert.Equal(expected, n.NormalizeDomain(host));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNull_returns_UnknownTag(string? host)
    {
        var n = New();
        Assert.Equal("unknown", n.NormalizeDomain(host));
        Assert.Equal("unknown", n.NormalizeHost(host));
    }

    [Fact]
    public void Idempotent_on_normalized_output()
    {
        var n = New();
        foreach (var host in new[] { "www.acme.co.uk", "auth.stylo.bot", "acme.com" })
        {
            var once = n.NormalizeDomain(host);
            var twice = n.NormalizeDomain(once);
            Assert.Equal(once, twice);
        }
    }

    [Fact]
    public void RequestScope_carries_both()
    {
        var n = New();
        var scope = n.Resolve("Auth.Stylo.Bot:443");
        Assert.Equal("stylo.bot", scope.Domain);
        Assert.Equal("auth.stylo.bot", scope.Host);
    }
}