using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

public class SbSummaryViewComponent : ViewComponent
{
    private readonly DetectionDataExtractor _extractor;

    public SbSummaryViewComponent(DetectionDataExtractor extractor)
    {
        _extractor = extractor;
    }

    public IViewComponentResult Invoke(string viewName = "Default", string? cssClass = null)
    {
        var model = HttpContext != null ? _extractor.Extract(HttpContext) : new DetectionDisplayModel();
        ViewBag.CssClass = cssClass;
        return View(viewName, model);
    }
}
