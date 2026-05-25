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
///     Reads the same fingerprint source the dashboard signature detail page
///     uses, so the visitor sees ONE radar polygon across both surfaces. The
///     source is <see cref="SessionStore"/> -- the CQRS projection of the
///     visitor's fingerprint that merges persisted history with the in-flight
///     session via the write-through buffer. That projection IS the "most
///     up-to-date fingerprint" by construction; we don't recombine here.
///     <see cref="ISessionStore"/> is a cold-path fallback for the post-restart
///     window before the projection has been hydrated. Both paths project via
///     the SAME <see cref="ClockAxesResolver.FromSessionVector"/> so the radar
///     polygon is byte-identical across the home card and the dashboard.
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
        // 1. Live accumulator -- this is what the dashboard signature page reads
        //    as its idx=0 "live" row. Reading the same source means the home
        //    card's radar matches the dashboard's radar for the same visitor.
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
            catch { /* live accumulator best-effort */ }
        }

        // 2. Persistent finalised session -- fallback when live accumulator is
        //    cold (post-restart, or returning visitor whose new session hasn't
        //    started yet).
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
