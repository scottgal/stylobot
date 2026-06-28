using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public class DashboardRoutingTests
{
    [Theory]
    [InlineData("",                 "traffic",  null)]
    [InlineData("/",                "traffic",  null)]
    [InlineData("visitors",         "visitors", null)]
    [InlineData("aspnet-pack/routes", "aspnet-pack", "routes")]
    [InlineData("Aspnet-Pack/Routes", "aspnet-pack", "routes")]
    public void ParseRowRef_extracts_area_and_optional_sub(string path, string area, string? sub)
    {
        var r = DashboardRoutingHelpers.ParseRowRef(path);
        r.Area.Should().Be(area);
        r.Sub.Should().Be(sub);
    }

    [Theory]
    [InlineData("traffic",             true)]
    [InlineData("aspnet-pack/routes",  true)]
    [InlineData("api/summary",         false)]
    [InlineData("auth/login",          false)]
    [InlineData("hub/negotiate",       false)]
    [InlineData("partials/something",  false)]
    [InlineData("",                    false)]
    [InlineData("a/b/c",               false)]
    public void IsDashboardRowPath_accepts_one_or_two_segment_non_api_paths(string rel, bool expected)
    {
        DashboardRoutingHelpers.IsDashboardRowPath(rel).Should().Be(expected);
    }

    [Theory]
    [InlineData("",                    "")]
    [InlineData("?tab=traffic",        "")]
    [InlineData("?tab=traffic&fp=x",   "?fp=x")]
    [InlineData("?fp=x&tab=traffic",   "?fp=x")]
    [InlineData("?fp=x",               "?fp=x")]
    public void StripTabParam_removes_tab_keeps_everything_else(string input, string expected)
    {
        DashboardRoutingHelpers.StripTabParam(input).Should().Be(expected);
    }
}
