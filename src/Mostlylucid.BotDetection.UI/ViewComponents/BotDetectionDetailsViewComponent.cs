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
            var (clockAxes, diag) = await ResolveClockAxesAsync(primarySig);
            if (clockAxes is not null)
                model = model with { ClockAxes = clockAxes };
            ViewData["YdDiag"] = diag;
        }

        return View(viewName, model);
    }

    // Concatenated trail of every branch we touched so the diag in the HTML
    // tells the whole story rather than reporting only the last failure.
    private string _diagTrail = "";

    private async Task<(double[]?, string)> ResolveClockAxesAsync(string primarySig)
    {
        // 1. LIVE in-flight session -- the dashboard's "idx=0 live" row. This
        //    only carries data once the orchestrator's wave-30
        //    SessionVectorContributor has run for the visitor; for clearly-human
        //    visitors the orchestrator usually quorum-exits BEFORE wave 30, so
        //    the live cache is empty on first visit. We try it first because
        //    when it IS hot the radar is as fresh as possible.
        if (_liveStore is null)
        {
            _diagTrail += "live-null|";
        }
        else
        {
            try
            {
                var liveSession = _liveStore.GetCurrentSession(primarySig);
                if (liveSession is null)
                {
                    _diagTrail += "live-session-null|";
                }
                else if (liveSession.Count < 1)
                {
                    _diagTrail += $"live-count-{liveSession.Count}|";
                }
                else
                {
                    var vector = SessionVectorizer.Encode(liveSession);
                    if (vector.Length < 118)
                    {
                        _diagTrail += $"live-vec-{vector.Length}|";
                    }
                    else
                    {
                        var axes = ClockAxesResolver.FromSessionVector(vector);
                        if (axes is not null)
                            return (axes, $"live-ok-n{liveSession.Count}-len{vector.Length}");
                        _diagTrail += "live-axes-null|";
                    }
                }
            }
            catch (Exception ex)
            {
                return (null, _diagTrail + $"live-throw:{ex.GetType().Name}");
            }
        }

        // 2. PERSISTED most-recent session -- SessionAtomizerService runs every
        //    2 min and writes the visitor's finalised session vector to SQLite.
        //    This is the steady-state source: by the visitor's second page-load
        //    a finalised session almost always exists, and projecting from its
        //    stored vector is identical to what the dashboard renders on the
        //    signature detail page for the same visitor.
        if (_persistedStore is null)
        {
            return (null, _diagTrail + "persisted-null");
        }

        try
        {
            var sessions = await _persistedStore.GetSessionsAsync(
                primarySig, limit: 1, HttpContext!.RequestAborted);
            var latest = sessions.FirstOrDefault();
            if (latest is null)
                return (null, _diagTrail + "persisted-no-rows");
            if (latest.Vector is not { Length: > 0 } encoded)
                return (null, _diagTrail + "persisted-no-vector");

            var vector = SqliteSessionStore.DeserializeVector(encoded);
            if (vector.Length < 118)
                return (null, _diagTrail + $"persisted-vec-{vector.Length}");

            var axes = ClockAxesResolver.FromSessionVector(vector);
            return axes is not null
                ? (axes, _diagTrail + $"persisted-ok-len{vector.Length}")
                : (null, _diagTrail + $"persisted-axes-null-len{vector.Length}");
        }
        catch (Exception ex)
        {
            return (null, _diagTrail + $"persisted-throw:{ex.GetType().Name}");
        }
    }
}
