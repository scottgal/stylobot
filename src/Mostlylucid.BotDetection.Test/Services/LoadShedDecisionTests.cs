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
}
