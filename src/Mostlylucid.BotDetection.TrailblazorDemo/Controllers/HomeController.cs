using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.TrailblazorDemo.Models;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.TrailblazorDemo.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    public IActionResult Signals() => View();

    /// <summary>
    ///     Adaptive sitemap. Returns different content depending on the
    ///     visitor's bot verdict in <c>HttpContext.Items["BotDetection.AggregatedEvidence"]</c>.
    ///     Routed here as a controller action (instead of a minimal-API
    ///     endpoint at <c>/sitemap.xml</c>) because the MVC pipeline reliably
    ///     populates the detection evidence before the action runs, where
    ///     the minimal-API path was reading evidence as null.
    /// </summary>
    [HttpGet]
    [Route("/sitemap.xml")]
    public ContentResult Sitemap()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host.Value.TrimEnd('/')}";

        var evidence = HttpContext.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evObj)
            ? evObj as Mostlylucid.BotDetection.Orchestration.AggregatedEvidence
            : null;

        var probability = evidence?.BotProbability ?? 0d;
        var confidence = evidence?.Confidence ?? 0d;
        var riskBand = evidence?.RiskBand.ToString() ?? "Unknown";
        var isVerifiedCrawler = evidence?.Signals.TryGetValue("ua.is_verified_bot", out var vcObj) == true
                                && vcObj is bool vcBool && vcBool;

        string verdictLabel;
        string[] publicUrls;

        if (isVerifiedCrawler || probability < 0.4d)
        {
            verdictLabel = isVerifiedCrawler ? "verified-crawler" : "human";
            publicUrls = new[] { "/", "/Home/Signals", "/Home/Privacy" };
        }
        else if (probability >= 0.7d)
        {
            verdictLabel = "high-probability-bot";
            publicUrls = new[] { "/honeypot/admin" };
        }
        else
        {
            verdictLabel = "uncertain";
            publicUrls = new[] { "/", "/Home/Privacy" };
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        sb.Append($"  <!-- stylobot verdict: {verdictLabel} (risk={riskBand}, probability={probability:F2}, confidence={confidence:F2}) -->\n");
        foreach (var path in publicUrls)
        {
            sb.Append($"  <url><loc>{baseUrl}{path}</loc></url>\n");
        }
        sb.Append("</urlset>\n");

        return new ContentResult
        {
            Content = sb.ToString(),
            ContentType = "application/xml",
            StatusCode = 200
        };
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Subscribe(string email)
    {
        // Honeypot fields are checked server-side. If a trap was filled,
        // silently drop so the bot operator never learns we caught them.
        if (HoneypotValidator.IsTriggered(Request))
        {
            // Pretend it worked. Don't ban, don't 4xx, don't differentiate.
            return RedirectToAction(nameof(Subscribed));
        }

        // Real handling would go here.
        TempData["Email"] = email;
        return RedirectToAction(nameof(Subscribed));
    }

    public IActionResult Subscribed() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
}