using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Big-number rollup card. Composes <c>SbSparkline</c> for the trend and uses
///     the same health-band tokens as <c>SbHealthDot</c> for the left-border
///     accent so colour semantics stay consistent across the dashboard.
/// </summary>
public sealed class SbStatTileViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(StatTileViewModel model) => View(model);
}
