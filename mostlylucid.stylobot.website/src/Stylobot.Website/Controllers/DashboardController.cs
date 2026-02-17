using Microsoft.AspNetCore.Mvc;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

[Route("dashboard")]
public class DashboardController : Controller
{
    private readonly SeoService _seoService;

    public DashboardController(SeoService seoService)
    {
        _seoService = seoService;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();
        return View();
    }

    [HttpGet("signature/{signature}")]
    public IActionResult Signature(string signature)
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();
        ViewData["Signature"] = signature;
        return View();
    }
}
