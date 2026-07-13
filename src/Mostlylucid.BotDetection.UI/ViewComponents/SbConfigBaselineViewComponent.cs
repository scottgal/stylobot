using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     <c>sb-config-baseline</c>: the config-baseline layer of the /dashboard/policies effective
///     view. Renders the <c>BotTypeActionPolicies</c> + <c>DefaultActionPolicyName</c> enforcement
///     the SbPolicyStack rule list never showed, so a config-default throttle (the operator's
///     "silent throttle": <c>Scraper -> throttle-aggressive</c>) is visible with provenance and a
///     config key. Rendered above the rule list; the rule rendering is untouched.
///
///     <paramref name="canEdit"/> rides the same <see cref="IPolicyCanEditPolicy"/> gate as the
///     policy rows -- FOSS (AlwaysReadOnly) renders read-only; the commercial overlay flips it on
///     for a licensed dashboard-write owner and the config rows expose their edit affordance.
/// </summary>
public sealed class SbConfigBaselineViewComponent : ViewComponent
{
    private readonly EffectivePolicyComposer _composer;
    private readonly IPolicyCanEditPolicy _canEditPolicy;

    public SbConfigBaselineViewComponent(
        EffectivePolicyComposer composer,
        IPolicyCanEditPolicy canEditPolicy)
    {
        _composer = composer;
        _canEditPolicy = canEditPolicy;
    }

    public IViewComponentResult Invoke(bool? canEdit = null)
    {
        var effectiveCanEdit = canEdit ?? _canEditPolicy.CanEdit(HttpContext?.User);
        return View(_composer.ComposeConfigRows(effectiveCanEdit));
    }
}
