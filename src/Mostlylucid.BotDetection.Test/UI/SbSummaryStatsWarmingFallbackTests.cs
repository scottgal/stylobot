using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Locks the "pages NEVER load with empty data" rule (operator directive 2026-08-12)
///     on the summary strip's last-resort path: when the page stash is absent and the
///     self-fetch was served from the SWR store's cold placeholder (the warming signal
///     is stamped on this request), the strip must render the honest spinner — never a
///     false "0 req" beside real widgets. The pre-2026-08-12 behaviour painted the
///     placeholder zeros as if they were data.
/// </summary>
public sealed class SbSummaryStatsWarmingFallbackTests
{
    private static SbSummaryStatsViewComponent NewComponent(
        IDashboardEventStore store, HttpContext httpContext)
    {
        var vc = new SbSummaryStatsViewComponent(
            store,
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }),
            signatureCache: null,
            aggregateCache: null)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };
        return vc;
    }

    private static Mock<IDashboardEventStore> StoreReturningEmptySummary()
    {
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetSummaryAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                UniqueSignatures = 0,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new()
            });
        return store;
    }

    [Fact]
    public async Task Cold_signature_cache_populates_session_tiles_from_the_summary()
    {
        // The session-derived tiles (active-now, bounce, avg session) must fall back to
        // the gateway-computed session stats carried on the summary — a cacheless/cold
        // host never paints zero tiles beside real numbers (2026-08-13 defect).
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetSummaryAsync(
                It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 98813,
                BotRequests = 60000,
                HumanRequests = 38813,
                UncertainRequests = 0,
                UniqueSignatures = 158,
                BotFingerprints = 118,
                HumanFingerprints = 40,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new(),
                ActiveSessions = 3,
                BounceRate = 12.5,
                BotBounceRate = 20.0,
                HumanBounceRate = 5.0,
                AvgSessionDurationSecs = 42.0,
                BotAvgSessionDurationSecs = 30.0,
                HumanAvgSessionDurationSecs = 51.0
            });
        var httpContext = new DefaultHttpContext();
        // A cold-but-present empty cache exercises the 901e5a69 empty-cache branch.
        var emptyCache = new SignatureAggregateCache(new StyloBotDashboardOptions());
        var vc = new SbSummaryStatsViewComponent(
            store.Object,
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }),
            signatureCache: emptyCache,
            aggregateCache: null)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var result = await vc.InvokeAsync(audience: null, range: "24h");

        var view = Assert.IsType<ViewViewComponentResult>(result);
        var model = Assert.IsType<SummaryStatsModel>(view.ViewData!.Model);
        Assert.False(model.IsWarming);
        Assert.Equal(158, model.UniqueVisitors);
        Assert.Equal(3, model.ActiveSessions);
        Assert.Equal(12.5, model.BounceRate);
        Assert.Equal(20.0, model.BotBounceRate);
        Assert.Equal(5.0, model.HumanBounceRate);
        Assert.Equal(42.0, model.AvgSessionDurationSecs);
        Assert.Equal(51.0, model.HumanAvgSessionDurationSecs);
    }

    [Fact]
    public async Task Cold_placeholder_read_renders_the_spinner_not_zeros()
    {
        var httpContext = new DefaultHttpContext();
        DashboardWarmingSignal.MarkWarming(httpContext, "summary"); // the SWR store's stamp
        var vc = NewComponent(StoreReturningEmptySummary().Object, httpContext);

        var result = await vc.InvokeAsync(audience: null, range: "24h");

        var view = Assert.IsType<ViewViewComponentResult>(result);
        var model = Assert.IsType<SummaryStatsModel>(view.ViewData!.Model);
        Assert.True(model.IsWarming);
    }

    [Fact]
    public async Task Cold_placeholder_with_a_live_aggregate_prefers_real_numbers()
    {
        // The aggregate-cache fallback (c143f61b) outranks the spinner: when the
        // broadcaster's unwindowed snapshot has real data, serve it — the honest
        // spinner is only the last resort.
        var httpContext = new DefaultHttpContext();
        DashboardWarmingSignal.MarkWarming(httpContext, "summary");
        var store = StoreReturningEmptySummary();
        var aggregate = new DashboardAggregateCache();
        aggregate.Update(new DashboardAggregateCache.AggregateSnapshot
        {
            Countries = [],
            Endpoints = [],
            UserAgents = [],
            Summary = new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 129,
                BotRequests = 0,
                HumanRequests = 129,
                UncertainRequests = 0,
                UniqueSignatures = 129,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new()
            }
        });
        var vc = new SbSummaryStatsViewComponent(
            store.Object,
            Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }),
            signatureCache: null,
            aggregateCache: aggregate)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

        var result = await vc.InvokeAsync(audience: null, range: "24h");

        var view = Assert.IsType<ViewViewComponentResult>(result);
        var model = Assert.IsType<SummaryStatsModel>(view.ViewData!.Model);
        Assert.False(model.IsWarming);
        Assert.Equal(129, model.Summary.TotalRequests);
    }
}
