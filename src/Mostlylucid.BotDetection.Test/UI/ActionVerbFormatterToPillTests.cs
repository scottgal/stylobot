using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class ActionVerbFormatterToPillTests
{
    [Fact]
    public void Block_Pill_Has_Block_Intent_And_Empty_Detail()
    {
        var pill = ActionVerbFormatter.ToPill(new PolicyAction.Block(), PolicyIntentKind.Block, hasTrigger: false);
        Assert.Equal("block", pill.IconRef);
        Assert.Equal("BLOCK", pill.PillText);
        Assert.Equal("", pill.DetailText);
    }

    [Fact]
    public void RateLimit_Pill_Has_Throttle_Intent_And_Numeric_Detail()
    {
        var pill = ActionVerbFormatter.ToPill(new PolicyAction.RateLimit(60), PolicyIntentKind.Throttle, hasTrigger: false);
        Assert.Equal("throttle", pill.IconRef);
        Assert.Equal("THROTTLE", pill.PillText);
        Assert.Equal("to 60/min", pill.DetailText);
    }

    [Fact]
    public void Observe_Overlay_Wins_Over_Block_Subtype()
    {
        var pill = ActionVerbFormatter.ToPill(new PolicyAction.Block(), PolicyIntentKind.Observe, hasTrigger: false);
        Assert.Equal("observe", pill.IconRef);
        Assert.Equal("OBSERVE", pill.PillText);
        Assert.Equal("would block", pill.DetailText);
    }

    [Fact]
    public void Trigger_Appends_Hysteresis_Marker()
    {
        var pill = ActionVerbFormatter.ToPill(new PolicyAction.Throttle(10, "load"), PolicyIntentKind.Throttle, hasTrigger: true);
        Assert.Contains("hysteresis", pill.DetailText, System.StringComparison.OrdinalIgnoreCase);
    }
}
