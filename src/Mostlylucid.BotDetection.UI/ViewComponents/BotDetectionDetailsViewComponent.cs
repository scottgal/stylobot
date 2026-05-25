using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     View component for displaying bot detection results.
///     Works in two modes:
///     1. Inline with middleware: Reads from HttpContext.Items
///     2. Behind YARP proxy: Reads from X-Bot-Detection-* headers
///     <para>
///     Also enriches the model with <see cref="DetectionDisplayModel.ClockAxes"/>
///     from the same <see cref="ClockAxesResolver"/> the dashboard signature
///     detail page uses, so the marketing-site Your Detection radar and the
///     dashboard's render the visitor's behavioural shape from the same source.
///     Without this the card fell back to projecting detector contributions
///     onto the clock positions, which produced a visibly different polygon
///     than the dashboard's <c>RadarShape</c>-driven render -- two surfaces
///     showing two shapes for the same visitor.
///     </para>
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;
    private readonly VisitorListCache? _visitorCache;

    public BotDetectionDetailsViewComponent(
        DetectionDataExtractor extractor,
        VisitorListCache? visitorCache = null)
    {
        _extractor = extractor;
        _visitorCache = visitorCache;
    }

    public IViewComponentResult Invoke(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();

        // Enrich with the visitor's persisted 12-axis clock from the same source
        // the signature detail page uses. Lookup by the resolved primary signature;
        // when the visitor hasn't yet been cached the model stays with ClockAxes=null
        // and the view degrades gracefully (renders an empty radar rather than a
        // detector-contribution polygon that disagrees with the dashboard).
        var primarySig = model.Signatures?.PrimarySignature;
        if (!string.IsNullOrEmpty(primarySig) && _visitorCache is not null)
        {
            var visitor = _visitorCache.Get(primarySig);
            var clockAxes = ClockAxesResolver.FromRadarShape(visitor?.RadarShape);
            if (clockAxes is not null)
            {
                model = new DetectionDisplayModel
                {
                    IsBot = model.IsBot,
                    BotProbability = model.BotProbability,
                    Confidence = model.Confidence,
                    RiskBand = model.RiskBand,
                    BotType = model.BotType,
                    BotName = model.BotName,
                    PolicyName = model.PolicyName,
                    Action = model.Action,
                    ProcessingTimeMs = model.ProcessingTimeMs,
                    TopReasons = model.TopReasons,
                    DetectorContributions = model.DetectorContributions,
                    RawSignals = model.RawSignals,
                    FingerprintHash = model.FingerprintHash,
                    RequestId = model.RequestId,
                    Timestamp = model.Timestamp,
                    YarpCluster = model.YarpCluster,
                    YarpDestination = model.YarpDestination,
                    Signatures = model.Signatures,
                    ClockAxes = clockAxes,
                    UaFamily = visitor?.UaFamily ?? model.UaFamily,
                    CountryCode = visitor?.CountryCode ?? model.CountryCode,
                };
            }
        }

        return View(viewName, model);
    }
}
