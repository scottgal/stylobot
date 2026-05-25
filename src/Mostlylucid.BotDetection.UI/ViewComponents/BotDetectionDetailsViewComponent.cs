using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///     Enriches the model's ClockAxes by projecting the visitor's most-recent
///     persisted session vector through the same chain the dashboard signature
///     detail page uses (ClockAxesResolver.FromSessionVector). One persistent
///     source (ISessionStore, SQLite-backed on FOSS / Postgres on commercial),
///     one projection chain. No in-memory-only stores.
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
                var sessions = await _sessionStore.GetSessionsAsync(
                    primarySig, limit: 1, HttpContext!.RequestAborted);
                var latest = sessions.FirstOrDefault();
                if (latest?.Vector is { Length: > 0 } encoded)
                {
                    var vector = SqliteSessionStore.DeserializeVector(encoded);
                    var clockAxes = ClockAxesResolver.FromSessionVector(vector);
                    if (clockAxes is not null)
                        model = model with { ClockAxes = clockAxes };
                }
            }
            catch
            {
                // Persistent store best-effort: a transient lookup failure on the
                // home card must not break the page. ClockAxes stays null and the
                // view degrades to "no radar yet" instead of a fabricated shape.
            }
        }

        return View(viewName, model);
    }
}
