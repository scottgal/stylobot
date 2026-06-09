using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Tabular meter listing. One row per meter, with a dedicated sparkline column
///     so trend is visible without breaking the table grid. Used on the
///     <c>/dashboard/insights</c> global page and on per-pack hubs' "all meters
///     in this pack" panel. Composes <c>SbHealthDot</c> per row (band indicator
///     + left-edge accent) and <c>SbSparkline</c> per row (trend column).
/// </summary>
public sealed class SbMeterTableViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(MeterTableViewModel model) => View(model);
}
