using Microsoft.AspNetCore.Mvc.ViewComponents;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.ViewComponents;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public sealed class SbChartletViewComponentTests
{
    [Fact]
    public async Task InvokeAsync_returns_default_view_with_model()
    {
        var vc = new SbChartletViewComponent();
        var model = new ChartletViewModel(
            Id: "x",
            Kind: ChartletKind.StackedBar,
            BucketLabels: Array.Empty<string>(),
            Series: Array.Empty<ChartletSeries>(),
            Axes: new ChartletAxes("y", "number", "x", true),
            Drill: null);

        var result = await vc.InvokeAsync(model) as ViewViewComponentResult;

        Assert.NotNull(result);
        Assert.Same(model, result!.ViewData!.Model);
    }
}
