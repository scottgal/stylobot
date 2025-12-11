using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Stylobot.Website.Models;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

public class HomeController : Controller
{
    private readonly SeoService _seoService;

    public HomeController(SeoService seoService)
    {
        _seoService = seoService;
    }

    public IActionResult Index()
    {
        ViewData["SeoMetadata"] = _seoService.GetHomeMetadata();
        return View();
    }

    public IActionResult Privacy()
    {
        ViewData["SeoMetadata"] = _seoService.GetDefaultMetadata();
        return View();
    }

    public IActionResult Enterprise()
    {
        ViewData["SeoMetadata"] = _seoService.GetEnterpriseMetadata();
        return View();
    }

    public IActionResult Detectors()
    {
        ViewData["SeoMetadata"] = _seoService.GetDetectorsMetadata();
        return View();
    }

    public IActionResult Features()
    {
        ViewData["SeoMetadata"] = _seoService.GetFeaturesMetadata();
        return View();
    }

    public IActionResult Contact()
    {
        ViewData["SeoMetadata"] = _seoService.GetContactMetadata();
        return View();
    }

    [HttpGet]
    public IActionResult Time()
    {
        var html = $"<div class=\"p-4 bg-base-200 rounded\">Server time: {DateTime.Now:O}</div>";
        return Content(html, "text/html");
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
