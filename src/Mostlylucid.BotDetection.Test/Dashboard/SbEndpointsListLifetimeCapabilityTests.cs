using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Test.Helpers;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Render proof for the endpoint-window honesty seam: when the backing store
///     advertises lifetime-only endpoint aggregates (FOSS SQLite), the shared endpoint
///     list marks the view so it never prints a 24h label over lifetime counters.
///     The capability must reach the actual render, not just the store/API.
/// </summary>
public sealed class SbEndpointsListLifetimeCapabilityTests
{
    private static SbEndpointsListViewComponent NewVc(bool supportsWindowedEndpointStats)
    {
        var vc = new SbEndpointsListViewComponent(
            new DashboardAggregateCache(),
            new NullDashboardEventStore(supportsWindowedEndpointStats),
            Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" }));

        var httpContext = new DefaultHttpContext();
        vc.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = httpContext }
        };
        return vc;
    }

    [Fact]
    public async Task Lifetime_store_marks_the_list_as_lifetime()
    {
        var vc = NewVc(supportsWindowedEndpointStats: false);

        await vc.InvokeAsync();

        Assert.True((bool)vc.ViewData["EndpointsLifetimeOnly"]!);
    }

    [Fact]
    public async Task Windowed_store_does_not_mark_the_list_as_lifetime()
    {
        var vc = NewVc(supportsWindowedEndpointStats: true);

        await vc.InvokeAsync();

        Assert.False((bool)vc.ViewData["EndpointsLifetimeOnly"]!);
    }

    [Fact]
    public async Task Custom_range_passes_the_exact_from_to_pair_to_the_store()
    {
        var store = new NullDashboardEventStore();
        var vc = new SbEndpointsListViewComponent(
            new DashboardAggregateCache(), store,
            Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" }));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?from=2026-01-01&to=2026-01-03");
        vc.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = httpContext }
        };

        await vc.InvokeAsync(range: "custom");

        var (expectedStart, expectedEnd) = DashboardRangeScope.Resolve(httpContext, "custom");
        Assert.Equal(expectedStart, store.LastEndpointStart);
        Assert.Equal(expectedEnd, store.LastEndpointEnd);
        // Explicitly not the 24h fallback.
        Assert.NotEqual(DateTime.UtcNow.AddHours(-24).Date, store.LastEndpointStart!.Value.Date);
    }
}
