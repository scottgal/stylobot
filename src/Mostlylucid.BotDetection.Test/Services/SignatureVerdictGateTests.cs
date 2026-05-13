using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class SignatureVerdictGateTests
{
    private static SignatureCoordinator MakeCoordinator()
    {
        // SignatureCoordinator takes the full BotDetectionOptions; SignatureCoordinatorOptions
        // is nested under it. Use defaults; per-test priming controls the verdict.
        var opts = new BotDetectionOptions();
        return new SignatureCoordinator(
            NullLogger<SignatureCoordinator>.Instance,
            Options.Create(opts));
    }

    private static SignatureVerdictGate MakeGate(SignatureCoordinator coord)
        => new(coord, NullLogger<SignatureVerdictGate>.Instance);

    private static SignatureCacheOptions DefaultOpts() => new()
    {
        SkipMinConfidence = 0.85,
        SkipMaxAgeSeconds = 300,
        BiasMinConfidence = 0.30,
        BiasMaxAgeSeconds = 86_400,
        SkipSamplingRate = 0.0,
    };

    private static async Task PrimeCoordinator(SignatureCoordinator coord, string sig, int times, double prob)
    {
        for (var i = 0; i < times; i++)
            await coord.RecordRequestAsync(
                signature: sig,
                requestId: Guid.NewGuid().ToString("N"),
                path: "/",
                botProbability: prob,
                signals: new Dictionary<string, object>(),
                detectorsRan: new HashSet<string> { "Stub" });
        // The coordinator processes updates via a KeyedSequentialAtom; poll until the
        // recorded count appears on the snapshot. Mirrors SignatureCoordinatorVerdictTests.
        for (var i = 0; i < 50; i++)
        {
            var v = await coord.TryGetVerdictAsync(sig);
            if (v is not null && v.RequestCount >= times) return;
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task NoSignature_ReturnsMiss()
    {
        await using var coord = MakeCoordinator();
        var gate = MakeGate(coord);
        var result = await gate.DecideAsync(null, DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task NoCachedVerdict_ReturnsMiss()
    {
        await using var coord = MakeCoordinator();
        var gate = MakeGate(coord);
        var result = await gate.DecideAsync("sig-X", DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task ConfidentFreshVerdict_ReturnsSkip()
    {
        await using var coord = MakeCoordinator();
        // 20 observations push the inline confidence (Min(1, count/10)) to 1.0
        await PrimeCoordinator(coord, "sig-A", times: 20, prob: 0.9);
        var gate = MakeGate(coord);

        var result = await gate.DecideAsync("sig-A", DefaultOpts());
        Assert.Equal(GateAction.Skip, result.Action);
        Assert.NotNull(result.Verdict);
    }

    [Fact]
    public async Task LowConfidence_ReturnsBias_NotSkip()
    {
        await using var coord = MakeCoordinator();
        // 5 observations yields confidence 0.5: above Bias threshold (0.30), below Skip (0.85)
        await PrimeCoordinator(coord, "sig-B", times: 5, prob: 0.5);
        var gate = MakeGate(coord);

        var result = await gate.DecideAsync("sig-B", DefaultOpts());
        Assert.Equal(GateAction.Bias, result.Action);
    }

    [Fact]
    public async Task VeryLowConfidence_ReturnsMiss()
    {
        await using var coord = MakeCoordinator();
        // 2 observations: confidence 0.2 below BiasMinConfidence (0.30)
        await PrimeCoordinator(coord, "sig-C", times: 2, prob: 0.5);
        var gate = MakeGate(coord);

        var result = await gate.DecideAsync("sig-C", DefaultOpts());
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task Disabled_AlwaysMiss()
    {
        await using var coord = MakeCoordinator();
        await PrimeCoordinator(coord, "sig-D", times: 20, prob: 0.9);
        var gate = MakeGate(coord);

        var opts = DefaultOpts() with { Enabled = false };
        var result = await gate.DecideAsync("sig-D", opts);
        Assert.Equal(GateAction.Miss, result.Action);
    }

    [Fact]
    public async Task FullSampling_DowngradesSkipToBias()
    {
        await using var coord = MakeCoordinator();
        await PrimeCoordinator(coord, "sig-E", times: 20, prob: 0.9);
        var gate = MakeGate(coord);

        var opts = DefaultOpts() with { SkipSamplingRate = 1.0 };
        var result = await gate.DecideAsync("sig-E", opts);
        Assert.Equal(GateAction.Bias, result.Action);
    }

    [Fact]
    public async Task StaleVerdict_DoesNotSkip()
    {
        await using var coord = MakeCoordinator();
        await PrimeCoordinator(coord, "sig-F", times: 20, prob: 0.9);
        var gate = MakeGate(coord);

        // Force the test to see verdict as stale by tightening SkipMaxAgeSeconds.
        // The verdict was just observed so age is ~0; setting SkipMaxAgeSeconds to 0
        // means even instantaneous lookups fail the freshness check.
        var opts = DefaultOpts() with { SkipMaxAgeSeconds = 0 };
        var result = await gate.DecideAsync("sig-F", opts);
        // Stale-for-Skip but still in Bias window
        Assert.Equal(GateAction.Bias, result.Action);
    }
}
