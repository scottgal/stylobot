using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Period-selector chain lock for the visitors page (rebuild 2026-08-12): the page
///     previously read the traffic envelope at a FIXED 24h window — the scope bar's
///     clicked period never applied. The controller must resolve the baseline-applied
///     ?window= token through the single pinned-window derivation, so the clicked period's
///     bundle is what the page reads.
/// </summary>
public sealed class VisitorsControllerWindowTests
{
    private static VisitorsController NewController(
        Mock<IDashboardContentCache> cache,
        Action<DashboardPageWindow> capture,
        DashboardPageResult? page = null)
    {
        cache.Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .Callback<DashboardPageManifest, DashboardPageWindow, CancellationToken>((_, w, _) => capture(w))
            .ReturnsAsync(page ?? DashboardPageResult.Warming);
        return new VisitorsController(
            new Mock<IDashboardEventStore>().Object,
            cache.Object,
            new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardLayoutOptions { V2Enabled = true, DefaultTimeWindowMinutes = 1440 }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Index_reads_the_traffic_envelope_at_the_clicked_period()
    {
        DashboardPageWindow? captured = null;
        var controller = NewController(new Mock<IDashboardContentCache>(), w => captured = w);

        await controller.Index(null, null, null, null, null, null, "7d", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(120, captured.BucketMinutes);
        Assert.Equal(7 * 24 * 60, (captured.EndTime!.Value - captured.StartTime!.Value).TotalMinutes);
    }

    [Fact]
    public async Task Index_defaults_to_the_pinned_24h_window()
    {
        DashboardPageWindow? captured = null;
        var controller = NewController(new Mock<IDashboardContentCache>(), w => captured = w);

        await controller.Index(null, null, null, null, null, null, null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(20, captured.BucketMinutes);
        Assert.Equal(24 * 60, (captured.EndTime!.Value - captured.StartTime!.Value).TotalMinutes);
    }

    [Fact]
    public async Task Index_ignores_unknown_tokens_and_falls_back_to_24h()
    {
        DashboardPageWindow? captured = null;
        var controller = NewController(new Mock<IDashboardContentCache>(), w => captured = w);

        await controller.Index(null, null, null, null, null, null, "bogus", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(20, captured.BucketMinutes);
    }

    [Fact]
    public async Task Index_waits_for_the_first_paint_stash_on_a_pinned_window()
    {
        // The cold first-load 0 on the visitors counters strip (operator directive
        // 2026-08-12: pages NEVER load with empty data): a Warming envelope for the
        // PINNED default view holds the paint until the stash lands.
        var cache = new Mock<IDashboardContentCache>();
        cache.SetupSequence(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DashboardPageResult.Warming)
            .ReturnsAsync(DashboardPageResult.Warming)
            .ReturnsAsync(new DashboardPageResult(new DashboardDatasetBundle(
                Summary: null, TimeBuckets: null,
                BotAggregate: [new DashboardTopBotEntry { PrimarySignature = "sig", HitCount = 1 }],
                Geo: [], Endpoints: [])));

        var controller = new VisitorsController(
            new Mock<IDashboardEventStore>().Object,
            cache.Object,
            new DefaultDashboardPageManifestSource(),
            Options.Create(new DashboardLayoutOptions { V2Enabled = true, DefaultTimeWindowMinutes = 1440 }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        await controller.Index(null, null, null, null, null, null, "24h", CancellationToken.None);

        cache.Verify(c => c.GetCurrentAsync(
            It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
        Assert.False(((DashboardPageResult)controller.HttpContext.Items["sb.dashboard.pageresult"]!).IsWarming);
    }
}
