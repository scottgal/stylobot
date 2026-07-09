using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     IDashboardManifestContribution: packs add their widget key to a page's manifest
///     (composably, no replace/race) so the composer + tick materializer warm the pack
///     widget's dataset. The default source merges contributions, deduped, statics first.
/// </summary>
public sealed class DashboardManifestContributionTests
{
    [Fact]
    public void Merges_pack_contribution_into_the_traffic_manifest()
    {
        var source = new DefaultDashboardPageManifestSource(
            new[] { new FakeContribution("dashboard.traffic", new[] { "otel-mesh-services" }) });

        var manifest = source.For("dashboard.traffic");

        Assert.NotNull(manifest);
        Assert.Contains("site-health", manifest!.WidgetKeys);        // base keys retained
        Assert.Contains("otel-mesh-services", manifest.WidgetKeys);  // contribution merged in
    }

    [Fact]
    public void Dedupes_and_ignores_other_pages()
    {
        var source = new DefaultDashboardPageManifestSource(new[]
        {
            new FakeContribution("dashboard.traffic", new[] { "summary", "extra" }), // summary already present
            new FakeContribution("dashboard.visitors", new[] { "other-page-widget" }),
        });

        var traffic = source.For("dashboard.traffic")!;

        Assert.Single(traffic.WidgetKeys, k => k == "summary");             // deduped, not doubled
        Assert.Contains("extra", traffic.WidgetKeys);
        Assert.DoesNotContain("other-page-widget", traffic.WidgetKeys);     // other page not merged
    }

    [Fact]
    public void No_contributions_returns_the_base_manifest_unchanged()
    {
        var source = new DefaultDashboardPageManifestSource();
        var manifest = source.For("dashboard.traffic")!;
        Assert.DoesNotContain("otel-mesh-services", manifest.WidgetKeys);
    }

    private sealed class FakeContribution(string page, IReadOnlyList<string> keys) : IDashboardManifestContribution
    {
        public string PageKey => page;
        public IReadOnlyList<string> WidgetKeys => keys;
    }
}
