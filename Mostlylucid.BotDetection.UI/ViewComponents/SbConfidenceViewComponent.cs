using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

public class SbConfidenceViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;

    public SbConfidenceViewComponent(DetectionDataExtractor extractor)
    {
        _extractor = extractor;
    }

    public IViewComponentResult Invoke(string display = "bar", string width = "120px", string? cssClass = null)
    {
        var model = HttpContext != null ? _extractor.Extract(HttpContext) : new DetectionDisplayModel();
        ViewBag.Display = display;
        ViewBag.Width = width;
        ViewBag.CssClass = cssClass;
        return View(model);
    }
}
