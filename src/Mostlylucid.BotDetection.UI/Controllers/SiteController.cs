using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Owns the Site IA group.
///     <c>Index</c> renders the Site landing page (endpoints list + filter pills)
///     at <c>/dashboard/site</c>; <c>EndpointDetail</c> redirects the old
///     query-string URL to the real canonical endpoint detail page.
///     The middleware passes both URLs through to MVC via the
///     <c>site</c> / <c>site/...</c> case in <c>StyloBotDashboardMiddleware</c>.
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
        [FromQuery(Name = "bot_pressure")] string? botPressure,
        [FromQuery(Name = "window")] string? window)
    {
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        var model = new SitePageModel(
            Path: NullIfEmpty(path),
            Method: NullIfEmpty(method),
            Threat: NullIfEmpty(threat),
            BotPressure: NullIfEmpty(botPressure),
            BasePath: basePath,
            // Period selector (?window=) forwarded into SbEndpointsList so the component
            // takes the same parameter-driven branch the Traffic page's control takes —
            // the middleware-seeded first-paint reader (warm L2 bundle) instead of the
            // SWR cold path that rendered "Warming up" on first paint (2026-08-11 P0).
            Window: NullIfEmpty(window));

        return View("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
    }

    /// <summary>
    ///     Redirect the old query-string endpoint URL to the real canonical
    ///     detail page at /dashboard/endpoint/{method}/{path}. The old page
    ///     rendered a filtered endpoints list, not actual detail — it was
    ///     useless. Method defaults to GET (the endpoints list doesn't carry
    ///     method info). Operator directive 2026-08-11.
    /// </summary>
    [HttpGet("endpoint")]
    public IActionResult EndpointDetail(
        [FromQuery] string? path,
        [FromQuery] string? method)
    {
        if (string.IsNullOrWhiteSpace(path)) return RedirectToAction(nameof(Index));
        var resolvedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
        return Redirect($"/dashboard/endpoint/{resolvedMethod}/{Uri.EscapeDataString(path)}");
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
    string BasePath,
    // Active ?window= token (e.g. "6h"/"24h"/"7d"), or null for the dashboard default —
    // forwarded as SbEndpointsList's range so the list reads through the same
    // parameter-driven feed as the Traffic page (first-paint reader / windowed store).
    string? Window = null);
