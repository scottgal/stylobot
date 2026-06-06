using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public class FossDashboardGroupsTests
{
    [Fact]
    public void Groups_render_in_spec_order()
    {
        var ids = FossDashboardGroups.All.Select(g => g.Id).ToArray();
        ids.Should().Equal("live", "investigation", "policy", "system");
    }

    [Fact]
    public void Live_group_contains_overview_activity_visitors_in_order()
    {
        var live = FossDashboardGroups.All.Single(g => g.Id == "live");
        live.Rows.Select(r => r.Id).Should().Equal("overview", "activity", "visitors");
    }

    [Fact]
    public void System_group_marks_compliance_and_investigate_commercial_only()
    {
        var system = FossDashboardGroups.All.Single(g => g.Id == "system");
        system.Rows.Single(r => r.Id == "compliance").IsCommercialOnly.Should().BeTrue();
        system.Rows.Single(r => r.Id == "investigate").IsCommercialOnly.Should().BeTrue();
        system.Rows.Single(r => r.Id == "configuration").IsCommercialOnly.Should().BeFalse();
    }

    [Fact]
    public void All_visible_rows_have_unique_ids()
    {
        var ids = FossDashboardGroups.All.SelectMany(g => g.Rows).Select(r => r.Id).ToArray();
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Legacy_hidden_rows_carry_IsHidden_flag()
    {
        FossDashboardGroups.LegacyHidden.Should().NotBeEmpty();
        FossDashboardGroups.LegacyHidden.Should().OnlyContain(r => r.IsHidden);
    }

    [Fact]
    public void DashboardRowRef_default_targets_overview()
    {
        DashboardRowRef.Default.Area.Should().Be("overview");
        DashboardRowRef.Default.Sub.Should().BeNull();
    }
}
