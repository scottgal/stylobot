using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class ClassGateResolverTests
{
    private static readonly ClassGate HumanGate = new(MaxBotProb: 0.3, MinConfidence: 0.7);
    private static readonly ClassGate BotGate = new(MinBotProb: 0.5, MinConfidence: 0.7);

    [Fact]
    public void Null_prob_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(null, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void Null_conf_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.2, null, HumanGate, BotGate));
    }

    [Fact]
    public void Low_prob_high_conf_returns_human()
    {
        Assert.Equal(VisitorClass.Human, ClassGateResolver.Resolve(0.2, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void High_prob_high_conf_returns_bot()
    {
        Assert.Equal(VisitorClass.Bot, ClassGateResolver.Resolve(0.8, 0.9, HumanGate, BotGate));
    }

    [Fact]
    public void Low_prob_low_conf_returns_unknown_because_human_gate_requires_conf()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.1, 0.3, HumanGate, BotGate));
    }

    [Fact]
    public void High_prob_low_conf_returns_unknown_because_bot_gate_requires_conf()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.9, 0.3, HumanGate, BotGate));
    }

    [Fact]
    public void Borderline_prob_returns_unknown()
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(0.4, 0.9, HumanGate, BotGate));
    }

    [Theory]
    [InlineData(0.3, 0.7, VisitorClass.Human)]
    [InlineData(0.5, 0.7, VisitorClass.Bot)]
    public void Boundary_values_qualify_inclusively(double prob, double conf, VisitorClass expected)
    {
        Assert.Equal(expected, ClassGateResolver.Resolve(prob, conf, HumanGate, BotGate));
    }

    [Theory]
    [InlineData(double.NaN, 0.9)]
    [InlineData(0.2, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.9)]
    [InlineData(0.2, double.NegativeInfinity)]
    public void NaN_or_infinite_values_return_unknown(double prob, double conf)
    {
        Assert.Equal(VisitorClass.Unknown, ClassGateResolver.Resolve(prob, conf, HumanGate, BotGate));
    }
}
