using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
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

        // SSR: embed ALL tab data so every tab renders immediately.
        // XHR + SignalR provide live updates ONLY — never primary data.
        ViewData["SummaryJson"] = await SafeJson(() => LoadSummary());
        ViewData["CountriesJson"] = await SafeJson(() => LoadCountries());
        ViewData["ClustersJson"] = await SafeJson(() => LoadClusters());
        ViewData["TopBotsJson"] = await SafeJson(() => LoadTopBots());
        ViewData["VisitorsJson"] = await SafeJson(() => LoadVisitors());
        ViewData["UserAgentsJson"] = await SafeJson(() => LoadUserAgents());

        return View();
    }

    [HttpGet("signature/{signature}")]
    public async Task<IActionResult> Signature(string signature)
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();
        ViewData["Signature"] = signature;

        // SSR: embed signature data so the page never shows a spinner.
        ViewData["SignatureJson"] = await SafeJson(() => LoadSignatureDetail(signature));
        ViewData["DetectionsJson"] = await SafeJson(() => LoadSignatureDetections(signature));

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

    private async Task<string> LoadVisitors()
    {
        var signatures = await _eventStore.GetSignaturesAsync(50);
        return JsonSerializer.Serialize(signatures, CamelCase);
    }

    private Task<string> LoadUserAgents()
    {
        var cached = _aggregateCache.Current.UserAgents;
        if (cached.Count == 0) return Task.FromResult("[]");
        return Task.FromResult(JsonSerializer.Serialize(cached, CamelCase));
    }

    private async Task<string> LoadSignatureDetail(string signature)
    {
        // Find the signature in the signatures list
        var signatures = await _eventStore.GetSignaturesAsync(1000);
        var sig = signatures.FirstOrDefault(s =>
            string.Equals(s.PrimarySignature, signature, StringComparison.Ordinal));
        if (sig != null) return JsonSerializer.Serialize(sig, CamelCase);

        // Fallback: check top bots
        var bots = await _eventStore.GetTopBotsAsync(50);
        var bot = bots.FirstOrDefault(b =>
            string.Equals(b.PrimarySignature, signature, StringComparison.Ordinal));
        if (bot != null) return JsonSerializer.Serialize(bot, CamelCase);

        return "null";
    }

    private async Task<string> LoadSignatureDetections(string signature)
    {
        var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter
        {
            Limit = 50,
            SignatureId = signature
        });
        return JsonSerializer.Serialize(detections, CamelCase);
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
