using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///
///     <para>
///     Enrichment path matches what the dashboard's sessions API does for
///     the same signature:
///     1. Prefer the in-memory <see cref="SessionStore"/> live session
///        (this request's accumulating vector). The dashboard prepends a
///        synthetic <c>live=true</c> row sourced from the same store --
///        using it here means the home card's polygon for the current
///        request is the same vector the dashboard shows at idx=0.
///     2. Fall back to the most-recent finalised session from
///        <see cref="ISessionStore"/> when no live accumulator is warm
///        (e.g. post-restart, or a returning visitor whose new session
///        hasn't started yet).
///     Both paths run through <see cref="ClockAxesResolver.FromSessionVector"/>
///     so the radar polygon is byte-identical to whichever session the
///     dashboard surfaces for the same visitor.
///     </para>
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
        // 1. Live in-memory session -- same source the dashboard sessions API
        //    prepends as idx=0. Encoding here matches SessionVectorizer.Encode
        //    so the vector going into ClockAxesResolver is identical.
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

        // 2. Persisted most-recent finalised session.
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
