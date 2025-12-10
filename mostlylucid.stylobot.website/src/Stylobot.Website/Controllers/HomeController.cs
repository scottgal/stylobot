using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Stylobot.Website.Models;

namespace Stylobot.Website.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    public IActionResult Enterprise()
    {
        return View();
    }

    public IActionResult Detectors()
    {
        return View();
    }

    public IActionResult Features()
    {
        return View();
    }

    public IActionResult Contact()
    {
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
