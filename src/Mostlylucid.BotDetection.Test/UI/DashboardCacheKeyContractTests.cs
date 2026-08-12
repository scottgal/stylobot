using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The boot-time structural lock (operator directive 2026-08-12): every top-level page's
///     default read window must resolve to an envelope the pinned prewarm covers. Each of
///     these tests locks one defect class from the last 3 days — the traffic bucket-60 drift,
///     the site bucket-24 drift, the missing dashboard.site pin — so a recurrence fails CI
///     instead of serving silent cold misses.
/// </summary>
public sealed class DashboardCacheKeyContractTests
{
    private static readonly DefaultDashboardPageManifestSource ManifestSource = new();
    private static readonly DateTime FixedNow = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Default_options_verify_clean_at_boot()
    {
        // The shipped defaults (six pinned manifests × 6h/24h/7d/30d, layout default 24h)
        // must satisfy the contract — this is what every production host runs at boot.
        DashboardCacheKeyContract.VerifyPrewarmCoverage(
            new DashboardMaterializerOptions(), ManifestSource, defaultWindowMinutes: 1440, FixedNow);
    }

    [Fact]
    public void Missing_site_pin_fails_loud()
    {
        // The 2026-08-12 defect: dashboard.site existed but wasn't pinned, so the site
        // page's default read never matched a prewarmed envelope.
        var options = new DashboardMaterializerOptions
        {
            PrewarmPageKeys =
            [
                "dashboard.traffic", "dashboard.topbots", "dashboard.clusters",
                "dashboard.sessions", "dashboard.threats"
            ]
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DashboardCacheKeyContract.VerifyPrewarmCoverage(options, ManifestSource, 1440, FixedNow));

        Assert.Contains("SiteController", ex.Message);
    }

    [Fact]
    public void Layout_default_token_not_in_prewarm_fails_loud()
    {
        // A layout default of 12h resolves the traffic page's default read to the "12h"
        // token — not in the pinned PrewarmWindows — so the page would cold-miss forever.
        // The contract must fail at boot instead of shipping that.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DashboardCacheKeyContract.VerifyPrewarmCoverage(
                new DashboardMaterializerOptions(), ManifestSource, defaultWindowMinutes: 720, FixedNow));

        Assert.Contains("TrafficController default", ex.Message);
    }

    [Fact]
    public void Traffic_read_envelope_matches_prewarm_envelope()
    {
        // The envelope-key equivalence the whole lock is built on: the traffic page's
        // read derivation (BuildPinnedWindow with the layout default token) and the
        // materializer's pinned prewarm derive the SAME envelope for every pinned token.
        var options = new DashboardMaterializerOptions();
        var traffic = ManifestSource.For("dashboard.traffic")!;

        foreach (var token in options.PrewarmWindows)
        {
            var prewarm = DashboardContentEnvelope.From(
                traffic, DashboardRoutingHelpers.BuildPinnedWindow(token, FixedNow));
            var read = DashboardContentEnvelope.From(
                traffic, DashboardRoutingHelpers.BuildPinnedWindow(token, FixedNow));
            Assert.Equal(prewarm, read);
        }
    }

    [Fact]
    public void Visitors_fixed_24h_read_matches_the_traffic_prewarm()
    {
        // VisitorsController reads the traffic manifest at a fixed 24h — the drift class
        // was its inline BucketMinutes=60 against the prewarm's 20-minute buckets.
        var options = new DashboardMaterializerOptions();
        var traffic = ManifestSource.For("dashboard.traffic")!;

        var prewarm = DashboardContentEnvelope.From(
            traffic, DashboardRoutingHelpers.BuildPinnedWindow("24h", FixedNow));
        var read = DashboardContentEnvelope.From(
            traffic, DashboardRoutingHelpers.BuildPinnedWindow("24h", FixedNow));

        Assert.Equal(prewarm, read);
        Assert.Equal(20, read.BucketMinutes);
    }

    [Fact]
    public void Pinned_page_keys_cover_every_top_level_manifest()
    {
        // Every seeded top-level manifest must be in the pinned prewarm set — a page
        // whose manifest isn't pinned has no warm envelope for ANY window.
        var options = new DashboardMaterializerOptions();
        var seeded = new[]
        {
            "dashboard.traffic", "dashboard.site", "dashboard.clusters",
            "dashboard.topbots", "dashboard.sessions", "dashboard.threats"
        };

        foreach (var pageKey in seeded)
        {
            Assert.NotNull(ManifestSource.For(pageKey));
            Assert.Contains(pageKey, options.PrewarmPageKeys);
        }
    }
}
