using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class LoadShedDecisionTests
{
    private sealed class FakeSource(LoadBand band) : ILoadBandSource
    {
        public LoadBand CurrentBand { get; } = band;
    }

    private static LoadShedDecision New(LoadBand band) => new(new FakeSource(band));

    [Theory]
    [InlineData(LoadBand.Low)]
    [InlineData(LoadBand.Normal)]
    public void Never_sheds_at_low_or_normal_regardless_of_class(LoadBand band)
    {
        var decision = New(band);
        var opts = new LoadShedOptions();
        Assert.False(decision.ShouldShed(VisitorClass.Human, opts, 1));
        Assert.False(decision.ShouldShed(VisitorClass.Unknown, opts, 1));
        Assert.False(decision.ShouldShed(VisitorClass.Bot, opts, 1));
    }

    [Fact]
    public void Humans_never_shed_at_high_by_default()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, seed));
    }

    [Fact]
    public void Humans_never_shed_at_critical_by_default()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, seed));
    }

    [Fact]
    public void Bots_always_shed_at_high_by_default()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, seed));
    }

    [Fact]
    public void Bots_always_shed_at_critical_by_default()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, seed));
    }

    [Fact]
    public void Operator_can_opt_in_to_shedding_humans()
    {
        var decision = New(LoadBand.Critical);
        var opts = new LoadShedOptions { HumanShedAtCritical = 1.0 };
        for (var seed = 0; seed < 1000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Human, opts, seed));
    }

    [Fact]
    public void Unknown_class_sheds_at_configured_fraction_deterministically()
    {
        var decision = New(LoadBand.High);
        var opts = new LoadShedOptions { UnknownShedAtHigh = 0.5 };
        var shedCount = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Unknown, opts, seed)) shedCount++;
        // DeterministicBucket distributes hashes uniformly; +-3% tolerance.
        var observed = shedCount / (double)n;
        Assert.InRange(observed, 0.47, 0.53);
    }
}