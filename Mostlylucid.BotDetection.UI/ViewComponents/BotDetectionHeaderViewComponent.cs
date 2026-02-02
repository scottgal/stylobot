using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     Unified bot detection header - single slim bar combining your detection status
///     with a live ticker feed. Replaces the separate BotDetectionDetails (compact) and BotTicker.
/// </summary>
public class BotDetectionHeaderViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;

    public BotDetectionHeaderViewComponent(DetectionDataExtractor extractor)
    {
        _extractor = extractor;
    }

    public IViewComponentResult Invoke()
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();
        return View(model);
    }
}
