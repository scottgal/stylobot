using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2a: <see cref="DefaultDashboardPageManifestSource"/> seeds one manifest per
///     row that isn't already covered by "dashboard.traffic" (Clusters/TopBots/Sessions/
///     Threats), each declaring exactly the synthetic widget key
///     <see cref="DefaultDashboardPageComposer"/> checks for.
/// </summary>
public sealed class DashboardRowManifestSeedTests
{
    [Theory]
    [InlineData("dashboard.clusters", DashboardRowWidgetKeys.ClustersRaw)]
    [InlineData("dashboard.topbots", DashboardRowWidgetKeys.TopBotsRaw)]
    [InlineData("dashboard.sessions", DashboardRowWidgetKeys.SessionsRaw)]
    [InlineData("dashboard.threats", DashboardRowWidgetKeys.ThreatsRaw)]
    public void Seeded_manifest_declares_exactly_its_own_row_extra_key(string pageKey, string expectedWidgetKey)
    {
        var source = new DefaultDashboardPageManifestSource();

        var manifest = source.For(pageKey);

        Assert.NotNull(manifest);
        Assert.Equal(pageKey, manifest!.PageKey);
        Assert.Contains(expectedWidgetKey, manifest.WidgetKeys);
    }

    [Fact]
    public void Traffic_manifest_is_unchanged_by_the_new_row_manifests()
    {
        var source = new DefaultDashboardPageManifestSource();

        var traffic = source.For("dashboard.traffic");

        Assert.NotNull(traffic);
        Assert.Equal(
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health" },
            traffic!.WidgetKeys);
    }
}
