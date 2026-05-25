using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///
///     <para>
///     Enrichment path: pull the visitor's most-recent session vector from
///     <see cref="ISessionStore"/> -- the same source the dashboard's
///     <c>/api/sessions/signature/&lt;sig&gt;</c> endpoint uses -- and run it
///     through <see cref="ClockAxesResolver.FromSessionVector"/>. The
///     dashboard signature detail page now calls the same helper, so the
///     12-axis radar polygon for one visitor is byte-identical on both
///     surfaces (no more "different shape on the homepage vs the dashboard"
///     drift). When the visitor doesn't yet have a finalised session vector,
///     <see cref="DetectionDisplayModel.ClockAxes"/> stays null and the
///     view renders an empty radar.
///     </para>
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly ISessionStore? _sessionStore;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        ISessionStore? sessionStore = null)
    {
        _extractor = extractor;
        _sessionStore = sessionStore;
    }

    public async Task<IViewComponentResult> InvokeAsync(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        var primarySig = model.Signatures?.PrimarySignature;
        if (!string.IsNullOrEmpty(primarySig) && _sessionStore is not null)
        {
            try
            {
                // Pull the most-recent finalised session for this visitor. The dashboard
                // surface fetches up to 20 sessions; the Your Detection card only needs
                // the latest one, so limit=1 keeps the storage hit minimal.
                var sessions = await _sessionStore.GetSessionsAsync(primarySig, limit: 1, HttpContext!.RequestAborted);
                var latest = sessions.FirstOrDefault();
                if (latest?.Vector is { Length: > 0 } encodedVector)
                {
                    var sessionVector = SqliteSessionStore.DeserializeVector(encodedVector);
                    var clockAxes = ClockAxesResolver.FromSessionVector(sessionVector);
                    if (clockAxes is not null)
                    {
                        model = model with { ClockAxes = clockAxes };
                    }
                }
            }
            catch
            {
                // Session store lookup is best-effort. Falling through with ClockAxes
                // null is preferable to surfacing a partial-data outage on the home
                // page -- the radar degrades to empty axes rather than a stale shape.
            }
        }

        return View(viewName, model);
    }
}
