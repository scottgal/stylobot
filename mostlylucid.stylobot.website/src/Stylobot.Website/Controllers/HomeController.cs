using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.GeoDetection.Middleware;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;
using Stylobot.Website.Models;
using Stylobot.Website.Services;
using Microsoft.Extensions.Configuration;

namespace Stylobot.Website.Controllers;

public class HomeController : Controller
{
    private readonly SeoService _seoService;
    private readonly IGeoLocationService _geoService;
    private readonly VisitorListCache _visitorListCache;
    private readonly CountryReputationTracker? _countryTracker;
    private readonly BotClusterService? _clusterService;
    private readonly bool _exposeDiagnostics;

    public HomeController(
        SeoService seoService,
        IGeoLocationService geoService,
        VisitorListCache visitorListCache,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CountryReputationTracker? countryTracker = null,
        BotClusterService? clusterService = null)
    {
        _seoService = seoService;
        _geoService = geoService;
        _visitorListCache = visitorListCache;
        _countryTracker = countryTracker;
        _clusterService = clusterService;
        _exposeDiagnostics = environment.IsDevelopment() ||
                             configuration.GetValue("StyloBot:ExposeDiagnostics", false) ||
                             bool.TryParse(Environment.GetEnvironmentVariable("STYLOBOT_EXPOSE_DIAGNOSTICS"), out var fromEnv) &&
                             fromEnv;
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
        return RedirectPermanent("/dashboard");
    }

    [HttpGet]
    public IActionResult Time()
    {
        if (!_exposeDiagnostics) return NotFound();
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
        if (!_exposeDiagnostics) return NotFound();

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

    // ===== HTMX Endpoints for LiveDemo =====

    [HttpGet("Home/LiveDemo/Visitors")]
    public IActionResult LiveDemoVisitors(string? filter, string sort = "lastSeen", string dir = "desc")
    {
        var visitors = _visitorListCache.GetFiltered(filter, sort, dir);
        return PartialView("_VisitorList", visitors);
    }

    [HttpGet("Home/LiveDemo/Visitor/{signature}")]
    public IActionResult LiveDemoVisitor(string signature)
    {
        var visitor = _visitorListCache.Get(signature);
        if (visitor == null) return NotFound();
        return PartialView("_VisitorRow", visitor);
    }

    [HttpGet("Home/LiveDemo/FilterCounts")]
    public IActionResult LiveDemoFilterCounts()
    {
        return Json(_visitorListCache.GetCounts());
    }

    [HttpGet("Home/TopBots")]
    public IActionResult TopBots(int count = 5)
    {
        var bots = _visitorListCache.GetTopBots(count);
        return PartialView("_TopBots", bots);
    }

    [HttpGet("Home/TopCountries")]
    public IActionResult TopCountries(int count = 10)
    {
        if (_countryTracker == null)
            return Content("<div class='text-base-content/50 text-sm p-4'>Country tracking not available</div>", "text/html");

        var countries = _countryTracker.GetTopBotCountries(count);
        return PartialView("_TopCountries", countries);
    }

    [HttpGet("Home/Clusters")]
    public IActionResult Clusters()
    {
        if (_clusterService == null)
            return Content("<div class='text-base-content/50 text-sm p-4'>Cluster detection not available</div>", "text/html");

        var clusters = _clusterService.GetClusters();
        return PartialView("_Clusters", clusters);
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
