using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Medium-sized meter card. Sits between <c>SbStatTile</c> (compact) and a
///     full chart panel. Composes <c>SbHealthDot</c> for the band indicator and
///     <c>SbSparkline</c> for both the headline-inline trend and the wider trend
///     band below the headline. Used on per-pack hub pages and on the dashboard
///     overview's pack-health row.
/// </summary>
public sealed class SbTrendCardViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(TrendCardViewModel model) => View(model);
}
