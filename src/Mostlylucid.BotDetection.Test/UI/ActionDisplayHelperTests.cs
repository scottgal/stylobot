using Mostlylucid.BotDetection.UI.Helpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks in the friendly-name/color mapping for every action-policy name actually in use
///     across the dashboard (Config baseline + Visitors' Action column both read from the same
///     policy vocabulary). Rate-limit-* and allow previously fell through to the unstyled
///     default case, rendering as bare lowercase text with no badge -- this was the design-review
///     finding that Visitors' Action column looked inconsistent next to Policies' colored pills.
/// </summary>
public class ActionDisplayHelperTests
{
    [Theory]
    [InlineData("rate-limit-ai", "AI Rate Limit")]
    [InlineData("rate-limit-search", "Search Rate Limit")]
    [InlineData("rate-limit-social", "Social Rate Limit")]
    [InlineData("rate-limit-monitoring", "Monitoring Rate Limit")]
    [InlineData("throttle-tools", "Tool Throttle")]
    [InlineData("allow", "Allow")]
    [InlineData(null, "Allow")]
    [InlineData("", "Allow")]
    public void GetFriendlyName_covers_every_action_policy_name(string? action, string expected)
    {
        Assert.Equal(expected, ActionDisplayHelper.GetFriendlyName(action));
    }

    [Theory]
    [InlineData("block-hard", "text-error")]
    [InlineData("rate-limit-ai", "text-warning")]
    [InlineData("rate-limit-search", "text-warning")]
    [InlineData("throttle-tools", "text-warning")]
    [InlineData("allow", "text-success")]
    [InlineData("logonly", "text-success")]
    [InlineData(null, "text-success")]
    public void GetCssClass_applies_shared_dashboard_color_semantics(string? action, string expected)
    {
        Assert.Equal(expected, ActionDisplayHelper.GetCssClass(action));
    }

    [Theory]
    [InlineData("rate-limit-search", "bg-warning/20 text-warning")]
    [InlineData("allow", "bg-success/20 text-success")]
    [InlineData("logonly", "bg-success/20 text-success")]
    [InlineData(null, "bg-success/20 text-success")]
    public void GetBadgeCssClass_never_falls_through_to_an_invisible_badge_for_known_actions(string? action, string expected)
    {
        Assert.Equal(expected, ActionDisplayHelper.GetBadgeCssClass(action));
    }
}
