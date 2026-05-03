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
/// </summary>
public class BotDetectionDetailsViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;

    public BotDetectionDetailsViewComponent(DetectionDataExtractor extractor)
    {
        _extractor = extractor;
    }

    public IViewComponentResult Invoke(string viewName = "Default")
    {
        var context = HttpContext;
        var model = context != null ? _extractor.Extract(context) : new DetectionDisplayModel();
        return View(viewName, model);
    }
}
