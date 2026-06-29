using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     One view component, one Chart.js wrapper, every dashboard chart. The
///     server emits a stable <see cref="ChartletViewModel"/> shape; the partial
///     serialises it into the canvas dataset and the client-side
///     <c>sb-chartlet.js</c> Alpine factory hydrates it. Axes / legend / tooltip
///     / drill semantics live in one place so every tile that uses
///     <c>&lt;vc:sb-chartlet&gt;</c> looks and behaves the same.
/// </summary>
public sealed class SbChartletViewComponent : ViewComponent
{
    public Task<IViewComponentResult> InvokeAsync(ChartletViewModel model)
    {
        return Task.FromResult<IViewComponentResult>(View(model));
    }
}