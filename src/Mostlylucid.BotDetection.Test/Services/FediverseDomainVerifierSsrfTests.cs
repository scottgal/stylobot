using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     SSRF guard contract for <see cref="FediverseDomainVerifier"/>.
///
///     The verifier issues outbound HTTPS GETs to a domain harvested from an
///     untrusted User-Agent string. The guard MUST reject domains that could
///     resolve to internal infrastructure (IP literals, .local mDNS names,
///     RFC2606 reserved TLDs, malformed hostnames) before any DNS lookup. These
///     tests are the security contract -- adding a new accepted-domain case
///     here is a deliberate decision, not a quick fix to make a test pass.
/// </summary>
public class FediverseDomainVerifierSsrfTests
{
    [Theory]
    [InlineData("mastodon.social")]
    [InlineData("mas.to")]
    [InlineData("MASTODON.SOCIAL", "mastodon.social")]  // normalised lowercase
    [InlineData("mastodon.social.", "mastodon.social")] // trailing dot stripped
    [InlineData("misskey.io")]
    [InlineData("pleroma.example.org")]
    public void Accepts_ValidPublicHostnames(string input, string? expectedNormalized = null)
    {
        var ok = FediverseDomainVerifier.PassesSsrfGuard(input, out var normalized);

        Assert.True(ok);
        Assert.Equal(expectedNormalized ?? input.ToLowerInvariant().TrimEnd('.'), normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_EmptyOrWhitespace(string? input)
    {
        Assert.False(FediverseDomainVerifier.PassesSsrfGuard(input, out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("169.254.169.254")]   // AWS/Azure IMDS -- the classic SSRF target
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    public void Rejects_IpLiterals(string input)
    {
        Assert.False(FediverseDomainVerifier.PassesSsrfGuard(input, out _));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("mastodon.localhost")]
    [InlineData("internal.local")]
    [InlineData("server.internal")]
    [InlineData("nope.invalid")]
    [InlineData("foo.arpa")]
    [InlineData("bar.test")]
    [InlineData("doc.example")]
    public void Rejects_ReservedAndPrivateTlds(string input)
    {
        Assert.False(FediverseDomainVerifier.PassesSsrfGuard(input, out _));
    }

    [Theory]
    [InlineData("no-dot")]                   // single-label, must have at least one dot
    [InlineData(".leading-dot.example")]     // empty leading label
    [InlineData("trailing-hyphen-.example")] // label can't end in hyphen
    [InlineData("-leading-hyphen.example")]  // label can't start with hyphen
    [InlineData("under_score.example")]      // underscores not in DNS hostname grammar
    [InlineData("space inside.example")]
    [InlineData("a.b/path")]                 // path injection
    [InlineData("a.b?query")]                // query injection
    [InlineData("a.b:8080")]                 // port -- must be plain hostname
    public void Rejects_MalformedHostnames(string input)
    {
        Assert.False(FediverseDomainVerifier.PassesSsrfGuard(input, out _));
    }

    [Fact]
    public void Rejects_OverlongDomain()
    {
        // RFC1035: max 253 chars for a full domain name.
        var oversize = new string('a', 254) + ".example";
        Assert.False(FediverseDomainVerifier.PassesSsrfGuard(oversize, out _));
    }
}
