using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Pure-SVG single-trend sparkline. All polyline geometry is computed in the
///     partial; this view component is just the entry point. Designed to be embedded
///     inside <c>SbStatTile</c>, <c>SbTrendCard</c>, and <c>SbMeterTable</c> via
///     <c>await Component.InvokeAsync("SbSparkline", model)</c>.
/// </summary>
public sealed class SbSparklineViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(SparklineViewModel model) => View(model);
}
