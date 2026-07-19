using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfigBaselineProvider _baseline;
    private readonly IPolicyCanEditPolicy _canEditPolicy;
    private readonly IConfiguration _configuration;

    public SbConfigBaselineViewComponent(
        IConfigBaselineProvider baseline,
        IPolicyCanEditPolicy canEditPolicy,
        IConfiguration configuration)
    {
        _baseline = baseline;
        _canEditPolicy = canEditPolicy;
        _configuration = configuration;
    }

    // Async: the provider is the local composer (in-process) on a detection host, or the remote
    // reader (HTTP to the gateway) on a thin-client dashboard. Same rows either way.
    public async Task<IViewComponentResult> InvokeAsync(bool? canEdit = null)
    {
        var affordance = canEdit switch
        {
            true => PolicyEditAffordance.Editable,
            false => PolicyEditAffordance.Hidden,
            null => PolicyEditAffordanceResolver.Resolve(_canEditPolicy, HttpContext?.User, _configuration)
        };
        var effectiveCanEdit = affordance == PolicyEditAffordance.Editable;
        var rows = await _baseline.GetConfigRowsAsync(effectiveCanEdit, HttpContext?.RequestAborted ?? default);
        if (affordance == PolicyEditAffordance.ReadOnly)
        {
            rows = rows.Select(row => row.ConfigRow is null
                ? row
                : row with { ConfigRow = row.ConfigRow with { ShowReadOnlyEditAffordance = true } })
                .ToArray();
        }
        return View(rows);
    }
}
