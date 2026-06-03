using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Locks in the wire-in for Bug F. The detector itself is exercised by the
///     existing pipeline tests through DI; what's new is the policy entry that
///     puts it on the inline path. If somebody removes it from
///     <see cref="DetectionPolicy.Default"/> the live Amazonbot-impersonator
///     repro silently regresses, so guard it explicitly.
/// </summary>
public class VerifiedBotInlineIntegrationTests
{
    [Fact]
    public void DefaultPolicy_RegistersVerifiedBotInline_FastPath()
    {
        // Live trigger: sig 9z3avO7sKTd7NAYY896Yog on www.stylobot.net claimed
        // Amazonbot from 85.121.245.x (Hong Kong) -- not in Amazon's published
        // range. The full VerifiedBotContributor is excluded inline because
        // rDNS is too slow; the inline contributor runs only the IP-range half
        // (O(n) CIDR, no I/O) and must be in the default FastPath chain or the
        // first impersonator request gets a friendly verdict.
        var policy = DetectionPolicy.Default;

        Assert.Contains("VerifiedBotInline", policy.FastPathDetectors);
    }

    [Fact]
    public void DefaultPolicy_StillExcludes_FullVerifiedBot()
    {
        // The full VerifiedBotContributor (rDNS + FCrDNS) must stay in the
        // ExcludedDetectors set so its DNS lookups don't run inline. The
        // BackgroundEnrichmentService picks it up async.
        var policy = DetectionPolicy.Default;

        Assert.Contains("VerifiedBot", policy.ExcludedDetectors);
    }
}