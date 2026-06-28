using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     V1 (dashboard IA collapse plan): renders the Visitors landing page that
///     the V2 sidebar (Group 1) links to at <c>/dashboard/visitors</c>. The
///     filter pills + visitor table live in the existing
///     <c>SbVisitorList/Default.cshtml</c> partial — this controller binds the
///     URL filters (<c>?country=</c>, <c>?bot_type=</c>, <c>?threat=</c>,
///     <c>?fingerprint=</c>, <c>?internal=true</c>) into the model so the
///     SSR-first page renders with the active filter chips + Internal pill in
///     the same shape an HTMX swap of the same partial would produce.
/// </summary>
[Route("dashboard/visitors")]
public sealed class VisitorsController : Controller
{
    private readonly IOptions<DashboardLayoutOptions> _layout;

    public VisitorsController(IOptions<DashboardLayoutOptions> layout)
    {
        _layout = layout;
    }

    [HttpGet("")]
    public IActionResult Index(
        [FromQuery] string? filter,
        [FromQuery] string? country,
        [FromQuery(Name = "bot_type")] string? botType,
        [FromQuery] string? threat,
        [FromQuery] string? fingerprint,
        [FromQuery(Name = "internal")] bool @internal)
    {
        // The view consults Layout.BasePath for HTMX urls inside the partial via
        // Model.BasePath. We populate that the same way the middleware does so the
        // chips + pills produce the same URLs as an HTMX swap.
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        var model = new VisitorsPageModel(
            Filter: string.IsNullOrWhiteSpace(filter) ? "all" : filter,
            Country: NullIfEmpty(country),
            BotType: NullIfEmpty(botType),
            Threat: NullIfEmpty(threat),
            FingerprintId: NullIfEmpty(fingerprint),
            Internal: @internal,
            BasePath: basePath);

        return View("/Views/StyloBot/Dashboard/Visitors/Index.cshtml", model);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
///     URL-bound filter set surfaced on the Visitors landing page. The page view
///     wires these into an HTMX <c>hx-get</c> against
///     <c>/dashboard/partials/visitors</c> so the first-paint and subsequent
///     swaps share the same filter shape.
/// </summary>
public sealed record VisitorsPageModel(
    string Filter,
    string? Country,
    string? BotType,
    string? Threat,
    string? FingerprintId,
    bool Internal,
    string BasePath);