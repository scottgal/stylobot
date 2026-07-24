using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Stage 2b: the row/widget-key -> freshness-class map, and the generic
///     "which classes does this page key's bundle touch" derivation the MIN-cadence
///     invariant is built on. See <see cref="DashboardRefreshCadenceTests"/> for the
///     pure interval-computation function these classes feed.
/// </summary>
public sealed class DashboardRowFreshnessTests
{
    [Theory]
    [InlineData("summary")]
    [InlineData("countries")]
    [InlineData("endpoints")]
    [InlineData("site-health")]
    [InlineData("time-chart")]
    [InlineData("user-agents")]
    public void Aggregate_tolerant_widget_keys_classify_as_Aggregate(string widgetKey)
    {
        Assert.Equal(DashboardRowFreshnessClass.Aggregate, DashboardRowFreshness.ClassOf(widgetKey));
    }

    [Theory]
    [InlineData("top-bots")] // Traffic page's own BotAggregate widget -- also backs the Visitors row.
    [InlineData("visitors")]
    public void Live_sensitive_widget_keys_classify_as_Live(string widgetKey)
    {
        Assert.Equal(DashboardRowFreshnessClass.Live, DashboardRowFreshness.ClassOf(widgetKey));
    }

    [Fact]
    public void ClustersRaw_is_Aggregate()
    {
        Assert.Equal(DashboardRowFreshnessClass.Aggregate, DashboardRowFreshness.ClassOf(DashboardRowWidgetKeys.ClustersRaw));
    }

    [Theory]
    [InlineData(DashboardRowWidgetKeys.TopBotsRaw)]
    [InlineData(DashboardRowWidgetKeys.SessionsRaw)]
    [InlineData(DashboardRowWidgetKeys.ThreatsRaw)]
    public void TopBots_Sessions_Threats_raw_keys_are_Live(string widgetKey)
    {
        Assert.Equal(DashboardRowFreshnessClass.Live, DashboardRowFreshness.ClassOf(widgetKey));
    }

    [Fact]
    public void Unknown_widget_key_fails_safe_to_Live_never_Aggregate()
    {
        // The one invariant that must never be violated is under-serving. An unrecognized
        // widget key (e.g. a future row this map hasn't been updated for yet) must default
        // to the FASTER class, not the slower one -- over-serving is fine, under-serving
        // is the one thing that must never happen.
        Assert.Equal(DashboardRowFreshnessClass.Live, DashboardRowFreshness.ClassOf("some-future-widget-key"));
    }

    [Fact]
    public void ClassesTouchedBy_returns_only_Aggregate_for_an_all_aggregate_manifest()
    {
        var manifest = new DashboardPageManifest("dashboard.clusters", new[] { DashboardRowWidgetKeys.ClustersRaw });
        var classes = DashboardRowFreshness.ClassesTouchedBy(manifest);
        Assert.Equal(new[] { DashboardRowFreshnessClass.Aggregate }, classes);
    }

    [Fact]
    public void ClassesTouchedBy_returns_only_Live_for_an_all_live_manifest()
    {
        var manifest = new DashboardPageManifest("dashboard.topbots", new[] { DashboardRowWidgetKeys.TopBotsRaw });
        var classes = DashboardRowFreshness.ClassesTouchedBy(manifest);
        Assert.Equal(new[] { DashboardRowFreshnessClass.Live }, classes);
    }

    [Fact]
    public void ClassesTouchedBy_returns_both_for_the_real_traffic_manifest_shape()
    {
        // dashboard.traffic's real widget-key set (DefaultDashboardPageManifestSource):
        // mostly Aggregate, but "top-bots" is Live -- this is the generic derivation the
        // MIN-cadence invariant relies on, not a hardcoded "traffic is special" branch.
        var manifest = new DashboardPageManifest(
            "dashboard.traffic",
            new[] { "summary", "time-chart", "top-bots", "countries", "endpoints", "site-health" });

        var classes = DashboardRowFreshness.ClassesTouchedBy(manifest);

        Assert.Contains(DashboardRowFreshnessClass.Aggregate, classes);
        Assert.Contains(DashboardRowFreshnessClass.Live, classes);
        Assert.Equal(2, classes.Count);
    }
}
