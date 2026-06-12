using Mostlylucid.BotDetection.Llm.Tunnel;
using Xunit;

namespace Mostlylucid.BotDetection.Test.LlmTunnel;

/// <summary>
///     Direct coverage for the SSRF guard. The validator was previously
///     reached only through <see cref="LlmNodeImporter.ImportKey"/>, so the
///     bounds (empty / non-http schemes / IPv4 + IPv6 link-local / DNS-resolved
///     link-local / non-resolving hostnames) were exercised only via the one
///     "happy path" import test. This file pegs each bound directly so a
///     regression to any of them shows up here, not as an obscure SSRF.
/// </summary>
public class LlmEndpointUrlValidatorTests
{
    private const string Description = "Connection key TunnelUrl";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyOrWhitespace_Throws(string? url)
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate(url!, Description));
        Assert.Contains("is empty", ex.Message);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("relative/path")]
    public void Validate_NotAbsoluteUrl_Throws(string url)
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate(url, Description));
        Assert.Contains("not a valid absolute URL", ex.Message);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/")]
    [InlineData("gopher://example.com/")]
    [InlineData("ws://example.com/")]
    public void Validate_NonHttpScheme_Throws(string url)
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate(url, Description));
        Assert.Contains("must use http or https", ex.Message);
    }

    [Theory]
    [InlineData("http://169.254.169.254/")]                 // AWS / Azure / GCP metadata
    [InlineData("https://169.254.169.254/latest/meta-data")] // AWS imds v1
    [InlineData("http://169.254.0.1/")]                     // any 169.254/16 address
    [InlineData("http://169.254.255.254/")]                 // top of link-local block
    public void Validate_LinkLocalIpv4Literal_Throws(string url)
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate(url, Description));
        Assert.Contains("link-local", ex.Message);
    }

    [Theory]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fe80::abcd:1234]/")]
    public void Validate_LinkLocalIpv6Literal_Throws(string url)
    {
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate(url, Description));
        Assert.Contains("link-local", ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]               // loopback v4
    [InlineData("http://[::1]/")]                   // loopback v6
    [InlineData("http://192.168.1.10:11434/")]      // RFC1918 LAN GPU box
    [InlineData("http://10.0.0.42/")]               // RFC1918
    [InlineData("http://172.16.5.10/")]             // RFC1918
    [InlineData("https://gpu.lan/")]                // hostname (offline test bench)
    public void Validate_AllowedTargets_DoNotThrow(string url)
    {
        var uri = LlmEndpointUrlValidator.Validate(url, Description);
        Assert.Equal(new Uri(url), uri);
    }

    [Fact]
    public void Validate_UnresolvableHostname_DoesNotThrow()
    {
        // PR #30 behaviour: a connection key may be imported offline before
        // the tunnel is up. DNS-resolution failures must NOT bubble up as
        // FormatException; the connection itself fails safely later, and an
        // attacker who controls DNS could defeat any resolve-time check via
        // rebinding anyway. PR #29's original code threw here -- PR #30
        // explicitly converted the catch to swallow.
        var uri = LlmEndpointUrlValidator.Validate(
            "https://this-host-definitely-does-not-exist.invalid/",
            Description);
        Assert.Equal("this-host-definitely-does-not-exist.invalid", uri.Host);
    }

    [Fact]
    public void Validate_DescriptionPropagatedIntoErrorMessage()
    {
        // The "{description}" parameter lets the caller flag the source of
        // the bad URL (key vs config vs operator input). Stable assertion so
        // the message format change doesn't go silently.
        var ex = Assert.Throws<FormatException>(() =>
            LlmEndpointUrlValidator.Validate("http://169.254.169.254/", "MyCustomLabel"));
        Assert.StartsWith("MyCustomLabel ", ex.Message);
    }
}
