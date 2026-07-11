using System.Security.Cryptography;
using System.Text;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit;

/// <summary>
///     Payload-to-token HMAC binding (slice 1b). Proves the beacon payload is bound
///     to the browser token so an off-browser replay of a canned payload with a
///     captured token is rejected -- the "signed signals" contract overview ratified.
/// </summary>
public class BrowserFingerprintPayloadSignatureTests
{
    private static string Sign(string body, string token)
        => Convert.ToBase64String(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(body)));

    [Fact]
    public void ValidSignature_Verifies()
    {
        var body = "{\"t\":\"tok-abc\",\"v\":\"2.1.0\",\"engine\":{\"stackStyle\":\"v8\"}}";
        var sig = Sign(body, "tok-abc");

        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            body, "tok-abc", sig, new ClientSideOptions(), out var reason);

        Assert.True(ok, reason);
        Assert.Equal("verified", reason);
    }

    [Fact]
    public void TamperedBody_Rejected()
    {
        // Attacker captures a valid token + the signature over the REAL body, then
        // swaps the body to fabricate a human verdict. The binding must reject it.
        var realBody = "{\"engine\":{\"stackStyle\":\"v8\"},\"tail\":{\"webdriver\":1}}";
        var capturedSig = Sign(realBody, "tok-abc");
        var fakedBody = "{\"engine\":{\"stackStyle\":\"v8\"},\"tail\":{\"webdriver\":0}}";

        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            fakedBody, "tok-abc", capturedSig, new ClientSideOptions(), out var reason);

        Assert.False(ok);
        Assert.Equal("signature mismatch", reason);
    }

    [Fact]
    public void NoSignature_NotRequired_AllowedForRollout()
    {
        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            "{}", "tok", null, new ClientSideOptions { RequirePayloadSignature = false }, out _);

        Assert.True(ok);
    }

    [Fact]
    public void NoSignature_Required_Rejected()
    {
        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            "{}", "tok", null, new ClientSideOptions { RequirePayloadSignature = true }, out var reason);

        Assert.False(ok);
        Assert.Equal("signature required but absent", reason);
    }

    [Fact]
    public void SignaturePresent_NoToken_Rejected()
    {
        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            "{}", null, "abc123", new ClientSideOptions(), out _);

        Assert.False(ok);
    }

    [Fact]
    public void InvalidBase64Signature_Rejected()
    {
        var ok = BrowserFingerprintEndpointExtensions.VerifyPayloadSignature(
            "{}", "tok", "!!!not-base64!!!", new ClientSideOptions(), out var reason);

        Assert.False(ok);
        Assert.Equal("signature not valid base64", reason);
    }
}