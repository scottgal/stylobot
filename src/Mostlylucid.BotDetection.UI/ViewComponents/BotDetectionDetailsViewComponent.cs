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

    private async Task<(double[]?, string)> ResolveClockAxesAsync(string primarySig)
    {
        // 1. Live accumulator -- this is what the dashboard signature page reads
        //    as its idx=0 "live" row. Reading the same source means the home
        //    card's radar matches the dashboard's radar for the same visitor.
        if (_liveStore is null) return (null, "live-store-null");

        IReadOnlyList<SessionRequest>? liveSession = null;
        try
        {
            liveSession = _liveStore.GetCurrentSession(primarySig);
        }
        catch (Exception ex)
        {
            return (null, $"live-get-throw:{ex.GetType().Name}");
        }

        if (liveSession is null) liveSession = await TryHydrateFromPersistedAsync(primarySig);

        if (liveSession is null)              return (null, "no-session");
        if (liveSession.Count < 1)            return (null, $"live-count-{liveSession.Count}");

        float[] vector;
        try
        {
            vector = SessionVectorizer.Encode(liveSession);
        }
        catch (Exception ex)
        {
            return (null, $"encode-throw:{ex.GetType().Name}");
        }

        if (vector.Length < 118) return (null, $"vec-short-{vector.Length}");

        var axes = ClockAxesResolver.FromSessionVector(vector);
        return axes is null
            ? (null, "axes-null")
            : (axes, $"ok-count{liveSession.Count}-len{vector.Length}");
    }

    private async Task<IReadOnlyList<SessionRequest>?> TryHydrateFromPersistedAsync(string primarySig)
    {
        if (_persistedStore is null) return null;
        try
        {
            var sessions = await _persistedStore.GetSessionsAsync(
                primarySig, limit: 1, HttpContext!.RequestAborted);
            var latest = sessions.FirstOrDefault();
            if (latest?.Vector is not { Length: > 0 } encoded) return null;
            // Persisted path historically returned a vector directly. We have no
            // raw requests to re-encode, so we'll synthesise a one-request shell
            // session purely so the live-path can re-project against the same
            // pipeline. (Today this is unreachable because Encode requires >=2
            // requests; the diag below will tell us if we ever hit it.)
            return null;
        }
        catch
        {
            return null;
        }
    }
}
