using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

public class SbBadgeViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;

    public SbBadgeViewComponent(DetectionDataExtractor extractor)
    {
        _extractor = extractor;
    }

    public IViewComponentResult Invoke(string variant = "full", string? cssClass = null)
    {
        var model = HttpContext != null ? _extractor.Extract(HttpContext) : new DetectionDisplayModel();
        ViewBag.Variant = variant;
        ViewBag.CssClass = cssClass;
        return View(model);
    }
}
