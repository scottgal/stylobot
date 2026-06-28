using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Si1 (dashboard IA collapse plan): renders the Site landing page that the
///     V2 sidebar (Group 1) links to at <c>/dashboard/site</c>. The endpoints
///     list + filter pills live in the existing
///     <c>SbEndpointsList/Default.cshtml</c> partial -- this controller binds
///     the URL filters (<c>?path=</c>, <c>?method=</c>, <c>?threat=</c>,
///     <c>?bot_pressure=</c>) into the model so the SSR-first page renders
///     with the active filter chips in the same shape an HTMX swap of the
///     same partial would produce.
///
///     <para>
///     Legacy <c>/dashboard/endpoints</c> continues to render its own page via
///     the FOSS row registry (<see cref="UI.Dashboard.FossDashboardGroups"/>);
///     the M-phase migration step is what 301-redirects the old URL to the
///     new one, not Si1.
///     </para>
/// </summary>
[Route("dashboard/site")]
public sealed class SiteController : Controller
{
    private readonly IOptions<DashboardLayoutOptions> _layout;

    public SiteController(IOptions<DashboardLayoutOptions> layout)
    {
        _layout = layout;
    }

    [HttpGet("")]
    public IActionResult Index(
        [FromQuery] string? path,
        [FromQuery] string? method,
        [FromQuery] string? threat,
        [FromQuery(Name = "bot_pressure")] string? botPressure)
    {
        // The view populates Model.BasePath so the partial's HTMX urls + chip
        // links resolve under the dashboard mount, matching the same shape the
        // middleware uses. V2 / legacy share the same base path today; the
        // accessor is kept symmetrical with TrafficController + VisitorsController
        // so a future split (e.g. an embedded site) lands here without rewiring.
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        var model = new SitePageModel(
            Path: NullIfEmpty(path),
            Method: NullIfEmpty(method),
            Threat: NullIfEmpty(threat),
            BotPressure: NullIfEmpty(botPressure),
            BasePath: basePath);

        return View("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
///     URL-bound filter set surfaced on the Site landing page. The page view
///     forwards these into the SbEndpointsList view component so the
///     first-paint endpoint list matches what an HTMX swap of
///     <c>/dashboard/partials/endpoints</c> with the same filter set would
///     produce.
/// </summary>
public sealed record SitePageModel(
    string? Path,
    string? Method,
    string? Threat,
    string? BotPressure,
    string BasePath);