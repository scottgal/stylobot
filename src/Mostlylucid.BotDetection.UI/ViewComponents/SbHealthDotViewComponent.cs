using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Single-glance health indicator. Renders one coloured dot driven by a
///     <see cref="HealthBand"/> value. No JavaScript; pure server-rendered span.
///     Reused by pack-hub headers and embedded inside <c>SbStatTile</c> for the
///     left-border accent.
/// </summary>
public sealed class SbHealthDotViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(HealthDotViewModel model) => View(model);
}
