using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Services;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

[Route("dashboard")]
public class DashboardController : Controller
{
    private readonly SeoService _seoService;
    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly BotClusterService? _clusterService;
    private readonly ILogger<DashboardController> _logger;

    private static readonly JsonSerializerOptions CamelCase = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DashboardController(
        SeoService seoService,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        ILogger<DashboardController> logger,
        BotClusterService? clusterService = null)
    {
        _seoService = seoService;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _clusterService = clusterService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();

        // SSR: embed dashboard data so the page is never empty on first load.
        // XHR + SignalR provide live updates after initial render.
        ViewData["SummaryJson"] = await SafeJson(() => LoadSummary());
        ViewData["CountriesJson"] = await SafeJson(() => LoadCountries());
        ViewData["ClustersJson"] = await SafeJson(() => LoadClusters());
        ViewData["TopBotsJson"] = await SafeJson(() => LoadTopBots());

        return View();
    }

    [HttpGet("signature/{signature}")]
    public IActionResult Signature(string signature)
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();
        ViewData["Signature"] = signature;
        return View();
    }

    private async Task<string> LoadSummary()
    {
        var summary = await _eventStore.GetSummaryAsync();
        return JsonSerializer.Serialize(summary, CamelCase);
    }

    private async Task<string> LoadCountries()
    {
        var cached = _aggregateCache.Current.Countries;
        var dbCountries = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(50);
        var countries = dbCountries.Take(50).Select(db => new
        {
            countryCode = db.CountryCode,
            countryName = db.CountryName,
            botRate = db.BotRate,
            botCount = db.BotCount,
            humanCount = db.HumanCount,
            totalCount = db.TotalCount,
        }).ToList();
        return JsonSerializer.Serialize(countries, CamelCase);
    }

    private Task<string> LoadClusters()
    {
        if (_clusterService == null) return Task.FromResult("[]");
        var clusters = _clusterService.GetClusters()
            .Select(cl => new
            {
                clusterId = cl.ClusterId,
                label = cl.Label ?? "Unknown",
                description = cl.Description,
                type = cl.Type.ToString(),
                memberCount = cl.MemberCount,
                avgBotProb = Math.Round(cl.AverageBotProbability, 3),
                country = cl.DominantCountry,
                averageSimilarity = Math.Round(cl.AverageSimilarity, 3),
                temporalDensity = Math.Round(cl.TemporalDensity, 3)
            })
            .ToList();
        return Task.FromResult(JsonSerializer.Serialize(clusters, CamelCase));
    }

    private async Task<string> LoadTopBots()
    {
        var signatures = await _eventStore.GetSignaturesAsync(10);
        var bots = signatures
            .Where(s => s.IsKnownBot)
            .Take(10)
            .ToList();
        return JsonSerializer.Serialize(bots, CamelCase);
    }

    private async Task<string> SafeJson(Func<Task<string>> loader)
    {
        try
        {
            return await loader();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Dashboard SSR: failed to load data");
            return "null";
        }
    }
}
