using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.GeoDetection.Middleware;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;
using Stylobot.Website.Models;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

public class HomeController : Controller
{
    private readonly SeoService _seoService;
    private readonly IGeoLocationService _geoService;

    public HomeController(SeoService seoService, IGeoLocationService geoService)
    {
        _seoService = seoService;
        _geoService = geoService;
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

    public IActionResult LiveDemo()
    {
        ViewData["SeoMetadata"] = _seoService.GetLiveDemoMetadata();
        return View();
    }

    [HttpGet]
    public IActionResult Time()
    {
        var html = $"<div class=\"p-4 bg-base-200 rounded\">Server time: {DateTime.Now:O}</div>";
        return Content(html, "text/html");
    }

    [HttpGet]
    public async Task<IActionResult> GeoHint()
    {
        var source = "GeoDetection";
        GeoLocation? location = null;

        if (HttpContext.Items.TryGetValue(GeoRoutingMiddleware.GeoLocationKey, out var geoObj) &&
            geoObj is GeoLocation geoFromContext)
        {
            location = geoFromContext;
            source = "GeoRoutingMiddleware";
        }
        else
        {
            var ip = GetClientIp();
            if (!string.IsNullOrWhiteSpace(ip))
            {
                location = await _geoService.GetLocationAsync(ip, HttpContext.RequestAborted);
                source = "GeoLocationService";
            }
        }

        return Json(new
        {
            countryCode = location?.CountryCode,
            countryName = location?.CountryName,
            source
        });
    }

    [HttpGet]
    public IActionResult SystemFingerprint()
    {
        var fingerprintHash = HttpContext.Items.TryGetValue("BotDetection.FingerprintHash", out var hashObj)
            ? hashObj?.ToString()
            : null;

        var signatures = new Dictionary<string, string>();
        if (HttpContext.Items.TryGetValue("BotDetection.Signatures", out var signaturesObj) &&
            signaturesObj is string signaturesJson)
        {
            try
            {
                signatures = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(signaturesJson) ?? new Dictionary<string, string>();
            }
            catch
            {
                signatures = new Dictionary<string, string>();
            }
        }

        IReadOnlyDictionary<string, object>? signals = null;
        List<string> topReasons = [];
        if (HttpContext.Items.TryGetValue("BotDetection.AggregatedEvidence", out var evidenceObj) &&
            evidenceObj is AggregatedEvidence evidence)
        {
            signals = evidence.Signals;
            topReasons = evidence.Contributions
                .Where(c => !string.IsNullOrWhiteSpace(c.Reason))
                .OrderByDescending(c => Math.Abs(c.ConfidenceDelta * c.Weight))
                .Take(5)
                .Select(c => c.Reason!)
                .ToList();
        }

        return Json(new
        {
            source = "BotDetectionSystem",
            fingerprintHash,
            signatures,
            signals = signals ?? new Dictionary<string, object>(),
            topReasons
        });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private string? GetClientIp()
    {
        var forwardedFor = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwardedFor))
            return forwardedFor.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

        var cfConnectingIp = Request.Headers["CF-Connecting-IP"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(cfConnectingIp))
            return cfConnectingIp;

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
