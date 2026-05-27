using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Renders the BotDetectionDetails partial for the current request.
///     <para>
///     Strict parity with the dashboard signature-detail polygon: the
///     <c>/api/sessions/signature/{sig}</c> endpoint that
///     <c>_BehavioralEvolution.cshtml</c> reads inserts a synthetic "live"
///     row at index 0 sourced from the in-memory <see cref="SessionStore"/>
///     accumulator, then the persisted sessions newest-first. The chart's
///     default focused polygon is <c>visible[0]</c> -- the live row when
///     warm, else the most-recent finalised session. Both go through
///     <see cref="ClockAxesResolver.FromSessionVector"/>.
///     </para>
///     <para>
///     This view component mirrors that order exactly: live first via the
///     same <see cref="SessionVectorizer.Encode"/> + projection, persisted
///     second via <see cref="SqliteSessionStore.DeserializeVector"/> + the
///     same projection. No third tier (the 16-dim <c>RadarShape</c>
///     projection via <see cref="ClockAxesResolver.FromRadarShape"/> uses
///     different axis math and zeros the Markov hours, which produces a
///     polygon that diverges from what the detail page renders -- "same
///     fingerprint, two shapes" was caused by that fallback). When both
///     canonical sources miss the visitor sees the calibrating placeholder
///     in the view rather than a fabricated polygon.
///     </para>
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly SessionStore? _liveStore;
    private readonly ISessionStore? _persistedStore;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        SessionStore? liveStore = null,
        ISessionStore? persistedStore = null)
    {
        _extractor = extractor;
        _liveStore = liveStore;
        _persistedStore = persistedStore;
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
        // 1. LIVE in-flight accumulator -- the same idx=0 "live" row the
        //    signature detail page prepends to its sessions array. When this
        //    branch hits, the detail page's default focused polygon is built
        //    from the same vector via the same projection.
        if (_liveStore is not null)
        {
            try
            {
                var liveSession = _liveStore.GetCurrentSession(primarySig);
                if (liveSession is { Count: >= 1 })
                {
                    var vector = SessionVectorizer.Encode(liveSession);
                    if (vector.Length >= 118)
                    {
                        var axes = ClockAxesResolver.FromSessionVector(vector);
                        if (axes is not null) return axes;
                    }
                }
            }
            catch
            {
                // Live accumulator is best-effort -- fall through to persisted.
            }
        }

        // 2. PERSISTED most-recent session -- the newest finalised session,
        //    which is what the detail page renders as visible[0] when the
        //    live accumulator is cold (post-restart, post-atomisation tick,
        //    or for a signature the visitor isn't currently driving).
        if (_persistedStore is null) return null;

        try
        {
            var sessions = await _persistedStore.GetSessionsAsync(
                primarySig, limit: 1, HttpContext!.RequestAborted);
            var latest = sessions.FirstOrDefault();
            if (latest?.Vector is not { Length: > 0 } encoded) return null;

            var vector = SqliteSessionStore.DeserializeVector(encoded);
            if (vector.Length < 118) return null;

            return ClockAxesResolver.FromSessionVector(vector);
        }
        catch
        {
            return null;
        }
    }
}
