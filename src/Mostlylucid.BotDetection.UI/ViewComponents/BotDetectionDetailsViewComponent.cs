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
    private readonly VisitorListCache? _visitorCache;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        SessionStore? liveStore = null,
        ISessionStore? persistedStore = null,
        VisitorListCache? visitorCache = null)
    {
        _extractor = extractor;
        _liveStore = liveStore;
        _persistedStore = persistedStore;
        _visitorCache = visitorCache;
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
        // 1. LIVE in-flight session -- the dashboard's "idx=0 live" row. Only
        //    carries data once the orchestrator reaches wave-30
        //    SessionVectorContributor for the visitor; clearly-human visitors
        //    usually quorum-exit before that, so this branch misses on first
        //    visit. We try it first anyway because when it IS hot the radar is
        //    as fresh as possible.
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

        // 2. PERSISTED most-recent session -- SessionAtomizerService runs every
        //    2 min and writes the visitor's finalised session vector. This is
        //    the steady-state source; by the visitor's second page-load a
        //    finalised session almost always exists and the projection matches
        //    what the dashboard renders on the signature detail page.
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
                    if (vector.Length >= 118)
                    {
                        var axes = ClockAxesResolver.FromSessionVector(vector);
                        if (axes is not null) return axes;
                    }
                }
            }
            catch
            {
                // Fall through to the visitor-cache fallback below.
            }
        }

        // 3. CACHED RadarShape fallback. The VisitorListCache lives in process
        //    and is populated from every detection event the dashboard sees,
        //    including the 16-dim RadarShape the orchestrator emits BEFORE the
        //    session vector is finalised. SessionAtomizerService can wipe the
        //    live in-flight session every 2 min on its atomization tick; for
        //    a brief window after that tick the persisted store also has no
        //    matching row, and the radar visibly disappeared between renders.
        //    Reading RadarShape from the visitor cache always has something to
        //    project because the cache is updated on the same hot path that
        //    just rendered the page. Lower-resolution than the 118-dim
        //    session vector (Markov hours collapse to origin) but identical
        //    visual semantics via the shared ClockProjection.Compose12Axes.
        if (_visitorCache is not null)
        {
            try
            {
                var visitor = _visitorCache.Get(primarySig);
                if (visitor?.RadarShape is { Length: 16 } shape)
                    return ClockAxesResolver.FromRadarShape(shape);
            }
            catch
            {
                // Cache miss / shape uninitialised -- no projection available.
            }
        }

        return null;
    }
}
