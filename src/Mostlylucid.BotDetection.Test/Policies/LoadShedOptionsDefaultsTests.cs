using Mostlylucid.BotDetection.Policies;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies;

/// <summary>
///     Pins the per-policy shed defaults that express the contract:
///     humans never shed by default, bots always shed when the band
///     escalates, unknowns shed at the operator-tunable fractions.
/// </summary>
public sealed class LoadShedOptionsDefaultsTests
{
    private static readonly LoadShedOptions Defaults = new();

    [Fact]
    public void Human_gate_default_is_strict()
    {
        Assert.Equal(0.3, Defaults.HumanGate.MaxBotProb);
        Assert.Equal(0.7, Defaults.HumanGate.MinConfidence);
    }

    [Fact]
    public void Bot_gate_default_is_strict()
    {
        Assert.Equal(0.5, Defaults.BotGate.MinBotProb);
        Assert.Equal(0.7, Defaults.BotGate.MinConfidence);
    }

    [Fact]
    public void Humans_never_shed_by_default()
    {
        Assert.Equal(0.0, Defaults.HumanShedAtHigh);
        Assert.Equal(0.0, Defaults.HumanShedAtCritical);
    }

    [Fact]
    public void Bots_always_shed_when_band_escalates_by_default()
    {
        Assert.Equal(1.0, Defaults.BotShedAtHigh);
        Assert.Equal(1.0, Defaults.BotShedAtCritical);
    }

    [Fact]
    public void Unknown_default_fractions_preserve_legacy_dropfraction_meaning()
    {
        Assert.Equal(0.3, Defaults.UnknownShedAtHigh);
        Assert.Equal(0.7, Defaults.UnknownShedAtCritical);
    }

    [Fact]
    public void Legacy_dropfraction_fields_remain_for_backward_compat()
    {
        // These existed before the redesign. Operator configs that bound them
        // continue to compile, even though the runtime now reads the
        // class-specific UnknownShedAt* fields. Kept for migration grace.
        Assert.Equal(0.2, Defaults.DropFractionAtHigh);
        Assert.Equal(0.5, Defaults.DropFractionAtCritical);
    }
}