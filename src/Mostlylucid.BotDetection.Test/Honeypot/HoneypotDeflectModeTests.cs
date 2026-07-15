using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Moq;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Honeypot;

/// <summary>
///     Coverage for <see cref="HoneypotResponseMode.Deflect404"/>: the operator wanted a clear policy
///     to return a plain 404 for a honeypot path rather than a 200 fake, because "honeypot 200 ...
///     means they keep trying". Deflect mode serves a fast, honest 404 and skips the simulation-pack
///     engagement; EngagePack (the default) keeps the existing trap behaviour.
/// </summary>
public class HoneypotDeflectModeTests
{
    [Fact]
    public async Task Deflect404_mode_serves_a_404_and_skips_pack_engagement()
    {
        var ctx = MakeContext("/wp-login.php");
        var policy = MakePolicy(HoneypotResponseMode.Deflect404);

        var result = await policy.ExecuteAsync(ctx, Evidence());

        ctx.Response.StatusCode.Should().Be(404);
        result.StatusCode.Should().Be(404);
        result.Description.Should().Contain("deflect",
            "the deflect branch runs -- no 200 simulation-pack engagement that would invite retries");
        ctx.Response.Headers["X-StyloBot-Honeypot"].ToString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task EngagePack_mode_with_no_matching_pack_falls_back_to_404_via_the_engage_path()
    {
        var ctx = MakeContext("/wp-login.php");
        var policy = MakePolicy(HoneypotResponseMode.EngagePack);

        var result = await policy.ExecuteAsync(ctx, Evidence());

        ctx.Response.StatusCode.Should().Be(404);
        // The engage path reaches the pack responder first; the reason distinguishes it from deflect.
        result.Description.Should().Contain("fake response served");
        result.Description.Should().NotContain("deflect");
    }

    private static HoneypotResponseActionPolicy MakePolicy(HoneypotResponseMode mode)
    {
        var opts = new HoneypotDetectionOptions { ResponseMode = mode };
        opts.RateLimit.Enabled = false; // no jitter delay in the test
        var monitor = new Mock<IOptionsMonitor<HoneypotDetectionOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(opts);

        var registry = new Mock<ISimulationPackRegistry>();
        registry.Setup(r => r.GetLoadedPacks()).Returns(Array.Empty<SimulationPack>());

        var responder = new SimulationPackResponder(registry.Object, NullLogger<SimulationPackResponder>.Instance);
        return new HoneypotResponseActionPolicy(
            monitor.Object, responder, new HoneypotRateLimiter(),
            NullLogger<HoneypotResponseActionPolicy>.Instance);
    }

    private static DefaultHttpContext MakeContext(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.9");
        return ctx;
    }

    private static AggregatedEvidence Evidence() => new()
    {
        BotProbability = 1.0,
        Confidence = 0.9,
        RiskBand = RiskBand.VeryHigh
    };
}
