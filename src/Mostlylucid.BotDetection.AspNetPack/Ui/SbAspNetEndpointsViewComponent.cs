using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.AspNetPack.Inventory;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     Renders the <see cref="IAspNetEndpointInventory" /> snapshot as a
///     compact table. Read-only -- never accepts user input.
/// </summary>
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