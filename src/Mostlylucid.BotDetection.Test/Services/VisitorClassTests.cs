using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

public sealed class VisitorClassTests
{
    [Fact]
    public void VisitorClass_has_three_values_in_order_human_unknown_bot()
    {
        Assert.Equal(0, (int)VisitorClass.Human);
        Assert.Equal(1, (int)VisitorClass.Unknown);
        Assert.Equal(2, (int)VisitorClass.Bot);
    }

    [Fact]
    public void ClassGate_default_ctor_args_match_unconstrained_neutral()
    {
        var gate = new ClassGate();
        Assert.Equal(1.0, gate.MaxBotProb);
        Assert.Equal(0.0, gate.MinBotProb);
        Assert.Equal(0.0, gate.MinConfidence);
    }
}
