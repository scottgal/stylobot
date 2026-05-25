using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.EndpointPolicies;
using Mostlylucid.BotDetection.RateLimiting;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

/// <summary>
///     Renders the /dashboard?tab=policies surface: a single view of every
///     declared rule (detection-policy + rate-limit) so the operator can see
///     "what would happen if a Scraper hit this endpoint right now". Read-
///     only in this iteration; live bucket-state and per-rule hit counters
///     are tracked separately and surface in a follow-up.
/// </summary>
public class SbPoliciesTabViewComponent(
    IOptionsMonitor<DetectionPolicyOptions> detectionOptions,
    IOptionsMonitor<RateLimitOptions> rateLimitOptions,
    IOptions<StyloBotDashboardOptions> dashboardOptions)
    : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var model = new PoliciesTabModel
        {
            BasePath = dashboardOptions.Value.BasePath.TrimEnd('/'),
            DetectionPolicies = detectionOptions.CurrentValue,
            RateLimitOptions = rateLimitOptions.CurrentValue,
        };
        return View(model);
    }
}
