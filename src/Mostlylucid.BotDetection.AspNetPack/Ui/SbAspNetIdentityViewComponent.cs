using Microsoft.AspNetCore.Mvc;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     Renders the current request's <see cref="System.Security.Claims.ClaimsPrincipal" />.
///     Used by the dashboard's Identity sub-row so operators can quickly see
///     which principal is hitting the gateway right now. Read-only.
/// </summary>
public sealed class SbAspNetIdentityViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var user = HttpContext.User;
        var vm = new AspNetIdentityViewModel(
            IsAuthenticated: user.Identity?.IsAuthenticated == true,
            Name: user.Identity?.Name ?? "(anonymous)",
            AuthType: user.Identity?.AuthenticationType ?? "(none)",
            Claims: user.Claims.Select(c => new ClaimDescriptor(c.Type, c.Value)).ToList());
        return View(vm);
    }
}

public sealed record AspNetIdentityViewModel(
    bool IsAuthenticated,
    string Name,
    string AuthType,
    IReadOnlyList<ClaimDescriptor> Claims);

public sealed record ClaimDescriptor(string Type, string Value);