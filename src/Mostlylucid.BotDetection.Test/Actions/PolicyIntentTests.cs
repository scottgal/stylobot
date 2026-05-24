using Mostlylucid.BotDetection.Actions;

namespace Mostlylucid.BotDetection.Test.Actions;

/// <summary>
///     Phase-1 scaffolding for the policy-grammar work
///     (docs/plans/2026-05-24-policy-grammar-core-experience.md). Pins:
///     <list type="bullet">
///       <item>the <see cref="ActionType"/> -> <see cref="PolicyIntent"/> mapping</item>
///       <item>that existing policy classes report a sensible default intent
///             without having to override the new property</item>
///     </list>
/// </summary>
public class PolicyIntentTests
{
    [Theory]
    [InlineData(ActionType.Block,     PolicyIntent.Block)]
    [InlineData(ActionType.Throttle,  PolicyIntent.Throttle)]
    [InlineData(ActionType.Challenge, PolicyIntent.Challenge)]
    [InlineData(ActionType.LogOnly,   PolicyIntent.Pass)]
    [InlineData(ActionType.Redirect,  PolicyIntent.Block)]
    [InlineData(ActionType.Custom,    PolicyIntent.Throttle)]
    public void ActionType_MapsToExpectedIntent(ActionType type, PolicyIntent expected)
    {
        Assert.Equal(expected, type.ToIntent());
    }

    [Fact]
    public void BlockPolicy_ReportsBlockIntentByDefault()
    {
        var block = new BlockActionPolicy("block-test",
            new BlockActionOptions { StatusCode = 403, Message = "x" });
        Assert.Equal(PolicyIntent.Block, block.Intent);
    }

    [Fact]
    public void ThrottlePolicy_ReportsThrottleIntentByDefault()
    {
        var throttle = new ThrottleActionPolicy("throttle-test",
            new ThrottleActionOptions { BaseDelayMs = 100 });
        Assert.Equal(PolicyIntent.Throttle, throttle.Intent);
    }

    [Fact]
    public void LogOnlyPolicy_ReportsPassIntent()
    {
        // LogOnly carries the "Pass" intent -- detection ran, no action.
        var logOnly = new LogOnlyActionPolicy("log-test", new LogOnlyActionOptions());
        Assert.Equal(PolicyIntent.Pass, logOnly.Intent);
    }
}
