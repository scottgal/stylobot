using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
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
///     Regression for the site-page summary-0 P0 (2026-08-12): the site page's summary strip
///     painted zeros forever because (a) SiteController derived its page window with a
///     different bucket width than the materializer's pinned prewarm (permanent envelope
///     cold-miss) and (b) the stash gate required BotAggregate/Geo slices the site manifest
///     never composes. This locks the controller to the shared pinned-window derivation and
///     proves a complete site-shaped bundle gets stashed for the VCs.
/// </summary>
public sealed class SiteControllerSummaryStashTests
{
    private static readonly DefaultDashboardPageManifestSource ManifestSource = new();

    private static SiteController NewController(
        Mock<IDashboardContentCache> cacheMock, Mock<IDashboardEventStore>? storeMock = null)
    {
        var controller = new SiteController(
            Options.Create(new DashboardLayoutOptions { V2Enabled = true, DefaultTimeWindowMinutes = 1440 }),
            Options.Create(new StyloBotDashboardOptions()),
            storeMock?.Object ?? new Mock<IDashboardEventStore>().Object,
            cacheMock.Object,
            ManifestSource)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        return controller;
    }

    private static DashboardPageResult CompleteSitePage() =>
        new(new DashboardDatasetBundle(
            Summary: new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 129,
                BotRequests = 80,
                HumanRequests = 49,
                UncertainRequests = 0,
                UniqueSignatures = 129,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new()
            },
            TimeBuckets:
            [
                new DashboardTimeSeriesPoint
                {
                    Timestamp = DateTime.UtcNow, BotCount = 1, HumanCount = 2, TotalCount = 3
                }
            ],
            BotAggregate: null, // the site manifest never composes these two
            Geo: null,
            Endpoints:
            [
                new DashboardEndpointStats { Method = "GET", Path = "/", TotalCount = 5, LastSeen = DateTime.UtcNow }
            ]));

    [Fact]
    public async Task Index_requests_the_pinned_window_shape_and_stashes_a_complete_bundle()
    {
        DashboardPageManifest? capturedManifest = null;
        DashboardPageWindow? capturedWindow = null;
        var page = CompleteSitePage();
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .Callback<DashboardPageManifest, DashboardPageWindow, CancellationToken>(
                (m, w, _) => { capturedManifest = m; capturedWindow = w; })
            .ReturnsAsync(page);
        var controller = NewController(cacheMock);

        var result = await controller.Index(null, null, null, null, "24h", null, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        Assert.NotNull(capturedManifest);
        Assert.Equal("dashboard.site", capturedManifest.PageKey);

        // The window must be the SAME shape the materializer's pinned prewarm derives —
        // the chartlet bucket size (24h → 20 min), never a hand-rolled window/60 (24h → 24).
        Assert.NotNull(capturedWindow);
        Assert.Equal(1440, (capturedWindow.EndTime!.Value - capturedWindow.StartTime!.Value).TotalMinutes);
        Assert.Equal(20, capturedWindow.BucketMinutes);

        // And the requested envelope must be exactly what the prewarm warms for that token.
        var reconstructedNow = capturedWindow.StartTime.Value.AddMinutes(1440);
        Assert.Equal(
            DashboardContentEnvelope.From(capturedManifest, DashboardRoutingHelpers.BuildPinnedWindow("24h", reconstructedNow)),
            DashboardContentEnvelope.From(capturedManifest, capturedWindow));

        // The complete site-shaped bundle (Summary + Endpoints, no BotAggregate/Geo) is
        // stashed for the VCs — the summary strip reads real data instead of cold-fetching.
        Assert.Same(page, controller.HttpContext.Items["sb.dashboard.pageresult"]);
    }

    [Fact]
    public async Task Index_does_not_stash_a_warming_bundle()
    {
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DashboardPageResult.Warming);
        var controller = NewController(cacheMock);

        await controller.Index(null, null, null, null, "24h", null, CancellationToken.None);

        Assert.False(controller.HttpContext.Items.ContainsKey("sb.dashboard.pageresult"));
    }

    [Fact]
    public async Task Index_partial_returns_the_swap_fragment()
    {
        // Period-selector rebuild (2026-08-12): ?partial=1 is the scope-bar swap endpoint —
        // the #sb-site-body region refetches it after a period click and swaps outerHTML.
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CompleteSitePage());
        var controller = NewController(cacheMock);

        var result = await controller.Index(null, null, null, null, "7d", 1, CancellationToken.None);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.Equal("/Views/StyloBot/Dashboard/Site/Index.cshtml", partial.ViewName);
    }

    [Fact]
    public async Task Index_waits_for_the_first_paint_stash_on_a_pinned_window()
    {
        // The first-load 0 race (operator directive 2026-08-12): a first load after a
        // deploy races the materializer — the envelope reads Warming, and without the
        // wait the page falls through to the self-fetch and paints zeros. The controller
        // must hold the paint and re-read until the stash lands.
        var page = CompleteSitePage();
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .SetupSequence(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DashboardPageResult.Warming)
            .ReturnsAsync(page);
        var controller = NewController(cacheMock);

        var result = await controller.Index(null, null, null, null, "24h", null, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        // The first read was Warming; the wait re-read until the stash landed.
        cacheMock.Verify(c => c.GetCurrentAsync(
            It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
        Assert.Same(page, controller.HttpContext.Items["sb.dashboard.pageresult"]);
    }

    [Fact]
    public async Task Index_waits_for_the_first_paint_stash_on_the_pinned_12h_window()
    {
        // 12h joined the pinned prewarm set on 2026-08-14 (operator directive: every
        // standard window's chart must be prerendered — the old 12h exclusion left every
        // 12h switch cold-missing). The site controller now holds the paint for 12h like
        // any other pinned window.
        var page = CompleteSitePage();
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .SetupSequence(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DashboardPageResult.Warming)
            .ReturnsAsync(page);
        var controller = NewController(cacheMock);

        var result = await controller.Index(null, null, null, null, "12h", null, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        cacheMock.Verify(c => c.GetCurrentAsync(
            It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(2));
        Assert.Same(page, controller.HttpContext.Items["sb.dashboard.pageresult"]);
    }

    [Fact]
    public async Task Index_does_not_stash_an_incomplete_bundle()
    {
        // Summary present but the endpoints slice came back empty — the poisoning guard
        // must leave the VCs on their self-fetch paths instead of stashing partial data.
        var page = new DashboardPageResult(new DashboardDatasetBundle(
            Summary: new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 1,
                BotRequests = 0,
                HumanRequests = 1,
                UncertainRequests = 0,
                UniqueSignatures = 1,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new()
            },
            TimeBuckets: null, BotAggregate: null, Geo: null, Endpoints: []));
        var cacheMock = new Mock<IDashboardContentCache>();
        cacheMock
            .Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var controller = NewController(cacheMock);

        await controller.Index(null, null, null, null, "24h", null, CancellationToken.None);

        Assert.False(controller.HttpContext.Items.ContainsKey("sb.dashboard.pageresult"));
    }
}
