using Microsoft.AspNetCore.Mvc;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     Renders the current request's <see cref="System.Security.Claims.ClaimsPrincipal" />.
///     Used by the dashboard's Identity sub-row so operators can quickly see
///     which principal is hitting the gateway right now. Read-only.
///     <para>
///     Explicit <c>[ViewComponent(Name = "FossSbAspNetIdentity")]</c> -- see
///     the same note on <c>SbAspNetEndpointsViewComponent</c>: the commercial
///     pack defines a class with the same simple name in
///     <c>Stylobot.Commercial.AspNetPack.Ui</c>, and MVC's short-name
///     resolution throws when both load into the same host.
///     </para>
/// </summary>
[ViewComponent(Name = "FossSbAspNetIdentity")]
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