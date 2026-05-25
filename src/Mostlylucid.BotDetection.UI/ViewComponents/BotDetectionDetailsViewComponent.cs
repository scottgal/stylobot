using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///     Enriches the model with ClockAxes via the same SessionStore +
///     ClockAxesResolver chain the dashboard signature detail surface uses,
///     so the radar polygon for one visitor is identical across both sites.
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly ISessionStore? _persistedStore;
    private readonly SessionStore? _liveStore;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        ISessionStore? persistedStore = null,
        SessionStore? liveStore = null)
    {
        _extractor = extractor;
        _persistedStore = persistedStore;
        _liveStore = liveStore;
    }

    public async Task<IViewComponentResult> InvokeAsync(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        var primarySig = model.Signatures?.PrimarySignature;
        if (!string.IsNullOrEmpty(primarySig))
        {
            var clockAxes = await ResolveClockAxesAsync(primarySig);
            if (clockAxes is not null)
                model = model with { ClockAxes = clockAxes };
        }

        return View(viewName, model);
    }

    private async Task<double[]?> ResolveClockAxesAsync(string primarySig)
    {
        if (_liveStore is not null)
        {
            try
            {
                var liveSession = _liveStore.GetCurrentSession(primarySig);
                if (liveSession is { Count: >= 1 })
                {
                    var vector = SessionVectorizer.Encode(liveSession);
                    var axes = ClockAxesResolver.FromSessionVector(vector);
                    if (axes is not null) return axes;
                }
            }
            catch { /* live accumulator best-effort; fall through to persisted */ }
        }

        if (_persistedStore is not null)
        {
            try
            {
                var sessions = await _persistedStore.GetSessionsAsync(
                    primarySig, limit: 1, HttpContext!.RequestAborted);
                var latest = sessions.FirstOrDefault();
                if (latest?.Vector is { Length: > 0 } encoded)
                {
                    var vector = SqliteSessionStore.DeserializeVector(encoded);
                    return ClockAxesResolver.FromSessionVector(vector);
                }
            }
            catch { /* persisted lookup best-effort */ }
        }

        return null;
    }
}
