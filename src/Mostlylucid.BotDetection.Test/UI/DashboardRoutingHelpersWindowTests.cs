using Mostlylucid.BotDetection.UI.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     <see cref="DashboardRoutingHelpers.WindowTokenToMinutes"/> is the single token->minutes
///     mapping shared between <c>StyloBotDashboardMiddleware.BuildVisitorsPageWindow</c> (a real
///     request's window) and the materializer's Tier 1 pinned-prewarm construction (§7 of the
///     compose-batch-overload review) -- extracted so the two can never drift into computing
///     different windows for what's meant to be the same envelope.
/// </summary>
public sealed class DashboardRoutingHelpersWindowTests
{
    [Theory]
    [InlineData("15m", 15)]
    [InlineData("60m", 60)]
    [InlineData("1h", 60)]
    [InlineData("6h", 360)]
    [InlineData("12h", 720)]
    [InlineData("24h", 1440)]
    [InlineData("1d", 1440)]
    [InlineData("7d", 10080)]
    [InlineData("30d", 43200)]
    public void WindowTokenToMinutes_maps_known_tokens(string token, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, DashboardRoutingHelpers.WindowTokenToMinutes(token, fallbackMinutes: 999));
    }

    [Fact]
    public void WindowTokenToMinutes_falls_back_for_an_unknown_token()
    {
        Assert.Equal(999, DashboardRoutingHelpers.WindowTokenToMinutes("bogus", fallbackMinutes: 999));
    }
}
