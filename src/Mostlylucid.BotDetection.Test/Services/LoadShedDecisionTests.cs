using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public class LoadShedDecisionTests
{
    private sealed class FixedBandSensor : ILoadBandSource
    {
        public FixedBandSensor(LoadBand band) => CurrentBand = band;
        public LoadBand CurrentBand { get; }
    }

    [Fact]
    public void Default_NeverSheds_AtLowLoad()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Low));
        var opts = new LoadShedOptions();
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithFullDrop_AlwaysSheds()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 1.0 };
        for (var i = 0; i < 100; i++)
            Assert.True(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithZeroDrop_NeverSheds()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 0.0 };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Critical_WithFractionalDrop_ApproximatesFraction()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 0.5 };
        var shed = 0;
        for (var i = 0; i < 1000; i++)
            if (decision.ShouldShed(opts, requestSeed: i)) shed++;
        Assert.InRange(shed, 400, 600);
    }

    [Fact]
    public void High_UsesHighFraction_NotCriticalFraction()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.High));
        var opts = new LoadShedOptions
        {
            DropFractionAtHigh = 0.0,
            DropFractionAtCritical = 1.0,
        };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void Normal_NeverSheds_RegardlessOfOptions()
    {
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Normal));
        var opts = new LoadShedOptions
        {
            DropFractionAtHigh = 1.0,
            DropFractionAtCritical = 1.0,
        };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i));
    }

    [Fact]
    public void High_ProtectsKnownHumans_RegardlessOfFraction()
    {
        // Adaptive behaviour: at High band, humans are protected even when
        // DropFractionAtHigh = 1.0. The whole point of the hint is to keep
        // legitimate users on while the gateway sheds bot traffic.
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.High));
        var opts = new LoadShedOptions { DropFractionAtHigh = 1.0 };
        for (var i = 0; i < 100; i++)
            Assert.False(decision.ShouldShed(opts, requestSeed: i, ShedHint.LikelyHuman));
    }

    [Fact]
    public void High_ShedsKnownBots_MoreAggressivelyThanBaseFraction()
    {
        // Bot hint doubles the shed fraction (capped at 1.0). Base 0.2 →
        // bots see ~0.4; over 1000 samples expect well above 0.2 * 1000.
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.High));
        var opts = new LoadShedOptions { DropFractionAtHigh = 0.2 };

        var botShed = 0;
        var unknownShed = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (decision.ShouldShed(opts, requestSeed: i, ShedHint.LikelyBot)) botShed++;
            if (decision.ShouldShed(opts, requestSeed: i, ShedHint.Unknown)) unknownShed++;
        }
        Assert.True(botShed > unknownShed * 1.5,
            $"bots should be shed more aggressively than unknowns: bots={botShed}, unknowns={unknownShed}");
    }

    [Fact]
    public void Critical_IgnoresHint_LookupTooExpensive()
    {
        // At Critical band the LoadShedDecision ignores the hint -- by design,
        // the verdict-cache lookup itself is too expensive at this pressure.
        // Same seed + same fraction must produce the same outcome regardless
        // of hint.
        var decision = new LoadShedDecision(new FixedBandSensor(LoadBand.Critical));
        var opts = new LoadShedOptions { DropFractionAtCritical = 0.5 };
        for (var i = 0; i < 100; i++)
        {
            var withoutHint = decision.ShouldShed(opts, i, ShedHint.Unknown);
            var humanHint   = decision.ShouldShed(opts, i, ShedHint.LikelyHuman);
            var botHint     = decision.ShouldShed(opts, i, ShedHint.LikelyBot);
            Assert.Equal(withoutHint, humanHint);
            Assert.Equal(withoutHint, botHint);
        }
    }

    [Fact]
    public void DefaultOptions_SafeForProduction_NoExplicitConfigNeeded()
    {
        // Self-protection is opt-OUT: a default-constructed LoadShedOptions
        // already sheds 0.2 at High and 0.5 at Critical. A policy that doesn't
        // set LoadShed gets safe behaviour, not zero shedding.
        var opts = new LoadShedOptions();
        Assert.Equal(0.2, opts.DropFractionAtHigh);
        Assert.Equal(0.5, opts.DropFractionAtCritical);
    }
}
