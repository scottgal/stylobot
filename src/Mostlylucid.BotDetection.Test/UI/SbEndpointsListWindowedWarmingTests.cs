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
///     The 2026-08-13 windowed-partial P0: a period swap on the site page refetches the
///     row content with an explicit window. When the first-paint reader was absent (or
///     seeded from a cold compose), an EMPTY result suppressed the warming-signal check
///     (the old guard only ran on the store path) and the widget rendered the bare
///     "No endpoint analytics yet" — absence of knowledge encoded as knowledge of
///     absence. An empty result + a stamped warming signal must ALWAYS render the
///     spinner, whatever the source of the empty list.
/// </summary>
public sealed class SbEndpointsListWindowedWarmingTests
{
    private static SbEndpointsListViewComponent NewComponent(
        DashboardAggregateCache aggregateCache, IDashboardEventStore store, HttpContext httpContext)
        => new(aggregateCache, store, Options.Create(new StyloBotDashboardOptions { BasePath = "/dashboard" }))
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext }
            }
        };

    private static Mock<IDashboardEventStore> StoreReturningEmpty() =>
        new();

    private sealed class EmptyFirstPaintReader : IDashboardEndpointsFirstPaintReader
    {
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count, DateTime? startTime, DateTime? endTime, string? audienceFilter,
            IReadOnlyList<string>? domains, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<DashboardEndpointStats>());
    }

    [Fact]
    public async Task Empty_reader_result_with_a_stamped_warming_signal_renders_the_spinner()
    {
        // The P0 shape: the windowed partial renders with a seeded reader that returns
        // EMPTY (the cold compose), and the SWR store stamped the warming signal.
        var httpContext = new DefaultHttpContext();
        DashboardEndpointsFirstPaintContext.Set(httpContext, new EmptyFirstPaintReader());
        DashboardWarmingSignal.MarkWarming(httpContext, "endpoints");
        var vc = NewComponent(new DashboardAggregateCache(), StoreReturningEmpty().Object, httpContext);

        var result = await vc.InvokeAsync(range: "7d");

        var view = Assert.IsType<ViewViewComponentResult>(result);
        var model = Assert.IsType<EndpointsListModel>(view.ViewData!.Model);
        Assert.True(model.IsWarming);
    }

    [Fact]
    public async Task Empty_reader_result_without_a_stamp_renders_the_honest_empty_state()
    {
        // A genuinely-empty compose (no stamp) still renders the honest empty state —
        // the fix must not turn every empty window into a forever-spinner.
        var httpContext = new DefaultHttpContext();
        DashboardEndpointsFirstPaintContext.Set(httpContext, new EmptyFirstPaintReader());
        var vc = NewComponent(new DashboardAggregateCache(), StoreReturningEmpty().Object, httpContext);

        var result = await vc.InvokeAsync(range: "7d");

        var view = Assert.IsType<ViewViewComponentResult>(result);
        var model = Assert.IsType<EndpointsListModel>(view.ViewData!.Model);
        Assert.False(model.IsWarming);
    }
}
