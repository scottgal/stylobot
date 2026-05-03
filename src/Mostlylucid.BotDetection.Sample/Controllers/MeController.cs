using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class MeController : Controller
{
    public IActionResult Index()
    {
        // Pass the full detection result to the view so we can display it in both
        // the tag helpers and a custom raw-data panel side by side.
        var result = HttpContext.GetBotDetectionResult();
        ViewBag.IsBot          = HttpContext.IsBot();
        ViewBag.IsHuman        = HttpContext.IsHuman();
        ViewBag.IsVerifiedBot  = HttpContext.IsVerifiedBot();
        ViewBag.IsSearchEngine = HttpContext.IsSearchEngineBot();
        ViewBag.Probability    = HttpContext.GetBotProbability();
        ViewBag.Confidence     = HttpContext.GetBotConfidence();
        ViewBag.RiskBand       = HttpContext.GetRiskBand().ToString();
        ViewBag.BotType        = HttpContext.GetBotType()?.ToString() ?? "—";
        ViewBag.BotName        = HttpContext.GetBotName() ?? "—";
        ViewBag.Reasons        = result?.Reasons?.Select(r => r.Detail).ToList() ?? [];
        return View();
    }
}
