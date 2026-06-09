using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the dashboard overview pack-health row: a horizontal grid of
///     <c>SbStatTile</c>s, one per registered <see cref="IPackHealthSummaryProvider"/>
///     (C1 of the Pack Metrics + Cohesive Views plan).
///     <para>
///         Sits at the top of <c>_Overview.cshtml</c>. When no providers
///         contribute a tile the partial renders nothing and the existing
///         overview widgets below take over.
///     </para>
/// </summary>
public sealed class SbPackHealthRowViewComponent : ViewComponent
{
    private readonly DashboardOverviewPackHealthRowBuilder _builder;

    public SbPackHealthRowViewComponent(DashboardOverviewPackHealthRowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _builder = builder;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var model = await _builder.BuildAsync(HttpContext.RequestAborted);
        return View(model);
    }
}
