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
        var diag = new List<string>();
        diag.Add($"sig={primarySig ?? "<null>"}");
        diag.Add($"liveStore={_liveStore is null}");
        diag.Add($"persistedStore={_persistedStore is null}");

        if (!string.IsNullOrEmpty(primarySig))
        {
            var clockAxes = await ResolveClockAxesAsync(primarySig, diag);
            if (clockAxes is not null)
                model = model with { ClockAxes = clockAxes };
            diag.Add($"resolvedClockAxes={clockAxes is not null}");
        }

        // Diagnostic surface: marshal the resolution-path notes into RawSignals so the
        // view can emit them as an HTML comment for live inspection in the browser.
        var rawSignals = new Dictionary<string, object>(model.RawSignals)
        {
            ["__yourDetectionDiag"] = string.Join(" | ", diag)
        };
        model = model with { RawSignals = rawSignals };

        return View(viewName, model);
    }

    private async Task<double[]?> ResolveClockAxesAsync(string primarySig, List<string> diag)
    {
        if (_liveStore is not null)
        {
            try
            {
                var liveSession = _liveStore.GetCurrentSession(primarySig);
                diag.Add($"liveSession.Count={liveSession?.Count.ToString() ?? "<null>"}");
                if (liveSession is { Count: >= 1 })
                {
                    var vector = SessionVectorizer.Encode(liveSession);
                    diag.Add($"liveEncode.Length={vector.Length}");
                    var axes = ClockAxesResolver.FromSessionVector(vector);
                    diag.Add($"liveAxesNonNull={axes is not null}");
                    if (axes is not null) return axes;
                }
            }
            catch (Exception ex) { diag.Add($"liveEx={ex.GetType().Name}:{ex.Message}"); }
        }

        if (_persistedStore is not null)
        {
            try
            {
                var sessions = await _persistedStore.GetSessionsAsync(
                    primarySig, limit: 1, HttpContext!.RequestAborted);
                diag.Add($"persistedSessions.Count={sessions.Count}");
                var latest = sessions.FirstOrDefault();
                if (latest?.Vector is { Length: > 0 } encoded)
                {
                    diag.Add($"persistedVector.Length={encoded.Length}");
                    var vector = SqliteSessionStore.DeserializeVector(encoded);
                    diag.Add($"persistedDecoded.Length={vector.Length}");
                    return ClockAxesResolver.FromSessionVector(vector);
                }
                else
                {
                    diag.Add("persistedNoVector");
                }
            }
            catch (Exception ex) { diag.Add($"persistedEx={ex.GetType().Name}:{ex.Message}"); }
        }

        return null;
    }
}
