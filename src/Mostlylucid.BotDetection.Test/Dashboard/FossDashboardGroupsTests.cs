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
    public void Live_group_contains_traffic_and_visitors_after_M2()
    {
        // M2 collapsed Overview + Activity into Traffic; Live group now only
        // carries the two surviving top-level surfaces.
        var live = FossDashboardGroups.All.Single(g => g.Id == "live");
        live.Rows.Select(r => r.Id).Should().Equal("traffic", "visitors");
    }

    [Fact]
    public void Investigation_group_contains_only_site_after_M2()
    {
        // M2 deleted Sessions, Threats; renamed Endpoints -> Site.
        var inv = FossDashboardGroups.All.Single(g => g.Id == "investigation");
        inv.Rows.Select(r => r.Id).Should().Equal("site");
    }

    [Fact]
    public void System_group_no_longer_contains_insights_or_investigate_after_M2()
    {
        var system = FossDashboardGroups.All.Single(g => g.Id == "system");
        var ids = system.Rows.Select(r => r.Id).ToArray();
        ids.Should().NotContain("insights");
        ids.Should().NotContain("investigate");
        // Compliance stays as the only commercial-only row in the System group.
        system.Rows.Single(r => r.Id == "compliance").IsCommercialOnly.Should().BeTrue();
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
    public void DashboardRowRef_default_targets_traffic_after_M2()
    {
        // M2 made Traffic the canonical landing page; the default ref points
        // at it so synthesised refs land on the live surface.
        DashboardRowRef.Default.Area.Should().Be("traffic");
        DashboardRowRef.Default.Sub.Should().BeNull();
    }
}
