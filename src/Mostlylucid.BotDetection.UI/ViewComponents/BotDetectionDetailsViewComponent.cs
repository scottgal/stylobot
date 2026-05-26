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
        var trail = "";

        if (_liveStore is null) trail += "live-store-null|";
        else
        {
            try
            {
                var liveSession = _liveStore.GetCurrentSession(primarySig);
                if (liveSession is null) trail += "live-session-null|";
                else if (liveSession.Count < 1) trail += $"live-count-{liveSession.Count}|";
                else
                {
                    var vector = SessionVectorizer.Encode(liveSession);
                    if (vector.Length < 118) trail += $"live-vec-{vector.Length}|";
                    else
                    {
                        var axes = ClockAxesResolver.FromSessionVector(vector);
                        if (axes is not null)
                            return (axes, $"live-ok-n{liveSession.Count}-len{vector.Length}");
                        trail += "live-axes-null|";
                    }
                }
            }
            catch (Exception ex)
            {
                return (null, trail + $"live-throw:{ex.GetType().Name}:{ex.Message?.Substring(0, Math.Min(40, ex.Message.Length))}");
            }
        }

        if (_persistedStore is null) return (null, trail + "persisted-null");

        try
        {
            var sessions = await _persistedStore.GetSessionsAsync(
                primarySig, limit: 1, HttpContext!.RequestAborted);
            var latest = sessions.FirstOrDefault();
            if (latest is null) return (null, trail + "persisted-no-rows");
            if (latest.Vector is not { Length: > 0 } encoded) return (null, trail + "persisted-no-vector");

            var vector = SqliteSessionStore.DeserializeVector(encoded);
            if (vector.Length < 118) return (null, trail + $"persisted-vec-{vector.Length}");

            var axes = ClockAxesResolver.FromSessionVector(vector);
            return axes is not null
                ? (axes, trail + $"persisted-ok-len{vector.Length}")
                : (null, trail + $"persisted-axes-null-len{vector.Length}");
        }
        catch (Exception ex)
        {
            return (null, trail + $"persisted-throw:{ex.GetType().Name}:{ex.Message?.Substring(0, Math.Min(40, ex.Message.Length))}");
        }
    }
}
