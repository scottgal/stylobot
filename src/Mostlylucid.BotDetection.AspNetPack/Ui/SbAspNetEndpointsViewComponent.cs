using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.AspNetPack.Inventory;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     Renders the <see cref="IAspNetEndpointInventory" /> snapshot as a
///     compact table. Read-only -- never accepts user input.
///     <para>
///     Explicit <c>[ViewComponent(Name = "FossSbAspNetEndpoints")]</c> so the
///     class doesn't collide with the commercial pack's
///     <c>Stylobot.Commercial.AspNetPack.Ui.SbAspNetEndpointsViewComponent</c>
///     when both assemblies are loaded into the same MVC host (the website
///     stack runs the commercial Enterprise SKU + transitively references the
///     FOSS pack assembly). Same class simple name on both sides → MVC short-
///     name resolution throws "matched multiple types"; this attribute moves
///     the FOSS class to a distinct short name and frees the commercial pack
///     to keep the unprefixed name. Pack's <see cref="Mostlylucid.BotDetection.UI.Dashboard.DashboardSubRow.ViewComponentName"/>
///     points to the new prefixed name on the FOSS side.
///     </para>
/// </summary>
[ViewComponent(Name = "FossSbAspNetEndpoints")]
public sealed class SbAspNetEndpointsViewComponent : ViewComponent
{
    private readonly IAspNetEndpointInventory _inventory;

    public SbAspNetEndpointsViewComponent(IAspNetEndpointInventory inventory)
    {
        _inventory = inventory;
    }

    public IViewComponentResult Invoke()
    {
        var endpoints = _inventory.All();
        return View(new AspNetEndpointsViewModel(endpoints));
    }
}

public sealed record AspNetEndpointsViewModel(IReadOnlyList<AspNetEndpointDescriptor> Endpoints);