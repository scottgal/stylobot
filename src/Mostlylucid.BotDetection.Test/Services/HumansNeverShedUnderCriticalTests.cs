using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Contract pin: under sustained Critical pressure (any axis tripping),
///     a verified human visitor never sees a 503. Verified bots always get
///     shed. Unknown visitors shed at the configured fraction.
/// </summary>
public sealed class HumansNeverShedUnderCriticalTests
{
    private sealed class CriticalBandSource : ILoadBandSource
    {
        public LoadBand CurrentBand => LoadBand.Critical;
    }

    private static LoadShedDecision NewDecision() => new(new CriticalBandSource());

    [Fact]
    public void Human_class_passes_every_single_seed_at_critical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 10_000; seed++)
            Assert.False(decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed));
    }

    [Fact]
    public void Bot_class_is_shed_every_single_seed_at_critical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions();
        for (var seed = 0; seed < 10_000; seed++)
            Assert.True(decision.ShouldShed(VisitorClass.Bot, opts, requestSeed: seed));
    }

    [Fact]
    public void Unknown_class_shed_fraction_matches_UnknownShedAtCritical()
    {
        var decision = NewDecision();
        var opts = new LoadShedOptions { UnknownShedAtCritical = 0.7 };
        var shed = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Unknown, opts, requestSeed: seed)) shed++;
        var observed = shed / (double)n;
        Assert.InRange(observed, 0.67, 0.73);
    }

    [Fact]
    public void Operator_override_can_shed_humans_at_critical()
    {
        // Confirms the gate is configurable, not hardcoded.
        var decision = NewDecision();
        var opts = new LoadShedOptions { HumanShedAtCritical = 0.5 };
        var shed = 0;
        const int n = 10_000;
        for (var seed = 0; seed < n; seed++)
            if (decision.ShouldShed(VisitorClass.Human, opts, requestSeed: seed)) shed++;
        var observed = shed / (double)n;
        Assert.InRange(observed, 0.47, 0.53);
    }
}