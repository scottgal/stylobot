using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Regression guard for the "fail trips bot" class: a known-bot UA that we CANNOT
///     verify (no IP ranges loaded AND no rDNS channel, or a transient DNS failure) must
///     report <c>VerificationMethod == "none"</c> and IsVerified == false -- a MISSING
///     signal, NOT a spoof. The consumer (VerifiedBotAtom) treats "none" as unverified
///     (confidence impacted), never spoofed. Verifying against none is invalid.
/// </summary>
public sealed class VerifiedBotUnverifiableNotSpoofedTests
{
    private static VerifiedBotRegistry NewRegistry() => new(
        NullLogger<VerifiedBotRegistry>.Instance,
        new StubHttpClientFactory(),
        Options.Create(new VerifiedBotRegistryOptions()),
        new Mostlylucid.BotDetection.Test.Scheduling.Helpers.RecordingScheduleCoordinator());

    [Fact]
    public async Task VerifyBotAsync_reports_none_not_spoofed_when_no_channel_available()
    {
        // Fresh registry: the arcjet IP-range catalogue has NOT been downloaded, so an
        // IP-range-only bot (GPTBot: published CIDRs, no FCrDNS domains) has no usable
        // verification channel here. That is "couldn't verify", not "spoofed".
        var registry = NewRegistry();

        var result = await registry.VerifyBotAsync(
            "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.1; +https://openai.com/gptbot",
            "203.0.113.55");

        Assert.NotNull(result);
        // The load-bearing assertion: unverifiable resolves to "none", not a spoof verdict.
        Assert.Equal("none", result!.VerificationMethod);
        Assert.False(result.IsVerified);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }
}
