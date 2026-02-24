using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Stylobot.Website.Services;

namespace Stylobot.Website.Controllers;

/// <summary>
///     MVC controller that serves the StyloBot dashboard within the website's shared layout.
///     The dashboard partial/API endpoints are still served by the StyloBotDashboardMiddleware at /_stylobot.
/// </summary>
public class DashboardController : Controller
{
    private readonly SeoService _seoService;
    private readonly IDashboardEventStore _eventStore;
    private readonly VisitorListCache _visitorListCache;
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly BotClusterService? _clusterService;
    private readonly IConfiguration _configuration;

    private static readonly string? DashboardVersion = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public DashboardController(
        SeoService seoService,
        IDashboardEventStore eventStore,
        VisitorListCache visitorListCache,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        IConfiguration configuration,
        BotClusterService? clusterService = null)
    {
        _seoService = seoService;
        _eventStore = eventStore;
        _visitorListCache = visitorListCache;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _configuration = configuration;
        _clusterService = clusterService;
    }

    public async Task<IActionResult> Index(string? tab)
    {
        ViewData["SeoMetadata"] = _seoService.GetDashboardMetadata();

        var basePath = _configuration.GetValue("StyloBotDashboard:BasePath", "/_stylobot")!.TrimEnd('/');
        var hubPath = basePath + "/hub";
        tab ??= "overview";

        // Get CSP nonce
        var cspNonce = HttpContext.Items.TryGetValue("CspNonce", out var nonceObj) && nonceObj is string s && s.Length > 0
            ? s
            : Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        HttpContext.Items["CspNonce"] = cspNonce;

        // Build all partial models
        var (visitors, visitorTotal, _, _) = _visitorListCache.GetFiltered("all", "lastSeen", "desc", 1, 24);

        DashboardSummary summary;
        try { summary = await _eventStore.GetSummaryAsync(); }
        catch
        {
            summary = new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0
            };
        }

        List<DashboardCountryStats> countriesData;
        try
        {
            var cached = _aggregateCache.Current.Countries;
            countriesData = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(100);
        }
        catch { countriesData = []; }

        var allUserAgents = _aggregateCache.Current.UserAgents.Count > 0
            ? _aggregateCache.Current.UserAgents
            : await ComputeUserAgentsFallbackAsync();

        var model = new DashboardShellModel
        {
            CspNonce = cspNonce,
            BasePath = basePath,
            HubPath = hubPath,
            ActiveTab = tab,
            Version = DashboardVersion,
            Summary = new SummaryStatsModel { Summary = summary, BasePath = basePath },
            Visitors = new VisitorListModel
            {
                Visitors = visitors, Counts = _visitorListCache.GetCounts(),
                Filter = "all", SortField = "lastSeen", SortDir = "desc",
                Page = 1, PageSize = 24, TotalCount = visitorTotal, BasePath = basePath
            },
            YourDetection = BuildYourDetectionModel(basePath),
            Countries = BuildCountriesModel("total", "desc", 1, 20, countriesData, basePath),
            Clusters = BuildClustersModel(basePath),
            UserAgents = BuildUserAgentsModel("all", "requests", "desc", 1, 25, allUserAgents, basePath),
            TopBots = BuildTopBotsModel(1, 10, "hits", basePath)
        };

        return View(model);
    }

    private YourDetectionModel BuildYourDetectionModel(string basePath)
    {
        try
        {
            var sigService = HttpContext.RequestServices.GetService<MultiFactorSignatureService>();
            if (sigService == null || _visitorListCache == null)
                return new YourDetectionModel { HasData = false, BasePath = basePath };

            var sigs = HttpContext.Items["BotDetection.Signatures"] as MultiFactorSignatures
                       ?? sigService.GenerateSignatures(HttpContext);
            var visitor = _visitorListCache.Get(sigs.PrimarySignature);

            if (visitor == null)
                return new YourDetectionModel { HasData = false, Signature = sigs.PrimarySignature, BasePath = basePath };

            return new YourDetectionModel
            {
                HasData = true, IsBot = visitor.IsBot, BotProbability = visitor.BotProbability,
                Confidence = visitor.Confidence, RiskBand = visitor.RiskBand,
                ProcessingTimeMs = visitor.ProcessingTimeMs, DetectorCount = visitor.TopReasons.Count,
                Narrative = visitor.Narrative, TopReasons = visitor.TopReasons,
                Signature = sigs.PrimarySignature, ThreatScore = visitor.ThreatScore,
                ThreatBand = visitor.ThreatBand, BasePath = basePath
            };
        }
        catch { return new YourDetectionModel { HasData = false, BasePath = basePath }; }
    }

    private TopBotsListModel BuildTopBotsModel(int page, int pageSize, string sortBy, string basePath)
    {
        var allBots = _signatureCache.GetTopBots(page: 1, pageSize: _signatureCache.MaxEntries, sortBy: sortBy);
        var pagedBots = allBots.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new TopBotsListModel
        {
            Bots = pagedBots, Page = page, PageSize = pageSize,
            TotalCount = allBots.Count, SortField = sortBy, BasePath = basePath
        };
    }

    private static CountriesListModel BuildCountriesModel(string sortField, string sortDir, int page, int pageSize, List<DashboardCountryStats> all, string basePath)
    {
        IEnumerable<DashboardCountryStats> sorted = sortField.ToLowerInvariant() switch
        {
            "country" => sortDir == "asc" ? all.OrderBy(c => c.CountryCode) : all.OrderByDescending(c => c.CountryCode),
            "botrate" => sortDir == "asc" ? all.OrderBy(c => c.BotRate) : all.OrderByDescending(c => c.BotRate),
            _ => sortDir == "asc" ? all.OrderBy(c => c.TotalCount) : all.OrderByDescending(c => c.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new CountriesListModel
        {
            Countries = paged, BasePath = basePath, SortField = sortField, SortDir = sortDir,
            Page = page, PageSize = pageSize, TotalCount = all.Count
        };
    }

    private static UserAgentsListModel BuildUserAgentsModel(string filter, string sortField, string sortDir, int page, int pageSize, List<DashboardUserAgentSummary> all, string basePath)
    {
        IEnumerable<DashboardUserAgentSummary> filtered = filter switch
        {
            "browser" => all.Where(u => u.Category == "Browser"),
            "bot" => all.Where(u => u.BotRate > 0.5),
            "ai" => all.Where(u => u.Category is "AI" or "AiBot"),
            "tool" => all.Where(u => u.Category is "Tool" or "Scraper" or "MonitoringBot"),
            _ => all
        };
        var filteredList = filtered.ToList();
        IEnumerable<DashboardUserAgentSummary> sorted = sortField.ToLowerInvariant() switch
        {
            "family" => sortDir == "asc" ? filteredList.OrderBy(u => u.Family) : filteredList.OrderByDescending(u => u.Family),
            "botrate" => sortDir == "asc" ? filteredList.OrderBy(u => u.BotRate) : filteredList.OrderByDescending(u => u.BotRate),
            _ => sortDir == "asc" ? filteredList.OrderBy(u => u.TotalCount) : filteredList.OrderByDescending(u => u.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new UserAgentsListModel
        {
            UserAgents = paged, BasePath = basePath, Filter = filter, SortField = sortField,
            SortDir = sortDir, Page = page, PageSize = pageSize, TotalCount = filteredList.Count
        };
    }

    private ClustersListModel BuildClustersModel(string basePath)
    {
        var clusters = _clusterService?.GetClusters()
            .Select(cl => new ClusterViewModel
            {
                ClusterId = cl.ClusterId, Label = cl.Label ?? "Unknown", Description = cl.Description,
                Type = cl.Type.ToString(), MemberCount = cl.MemberCount,
                AvgBotProb = Math.Round(cl.AverageBotProbability, 3), Country = cl.DominantCountry,
                AverageSimilarity = Math.Round(cl.AverageSimilarity, 3),
                TemporalDensity = Math.Round(cl.TemporalDensity, 3),
                DominantIntent = cl.DominantIntent,
                AverageThreatScore = Math.Round(cl.AverageThreatScore, 3)
            }).ToList() ?? [];

        return new ClustersListModel { Clusters = clusters, BasePath = basePath };
    }

    private async Task<List<DashboardUserAgentSummary>> ComputeUserAgentsFallbackAsync()
    {
        var detections = await _eventStore.GetDetectionsAsync(new DashboardFilter { Limit = 500 });
        var uaGroups = new Dictionary<string, (int total, int bot, double confSum, DateTime lastSeen)>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in detections)
        {
            string? family = null;
            if (d.ImportantSignals != null)
            {
                if (d.ImportantSignals.TryGetValue("ua.family", out var ff)) family = ff?.ToString();
                if (string.IsNullOrEmpty(family) && d.ImportantSignals.TryGetValue("ua.bot_name", out var bn))
                    family = bn?.ToString();
            }
            if (string.IsNullOrEmpty(family)) family = "Unknown";

            if (!uaGroups.TryGetValue(family, out var g))
                g = (0, 0, 0, DateTime.MinValue);
            g.total++;
            if (d.IsBot) g.bot++;
            g.confSum += d.Confidence;
            if (d.Timestamp > g.lastSeen) g.lastSeen = d.Timestamp;
            uaGroups[family] = g;
        }

        return uaGroups.Select(kv => new DashboardUserAgentSummary
        {
            Family = kv.Key,
            Category = InferUaCategory(kv.Key),
            TotalCount = kv.Value.total, BotCount = kv.Value.bot, HumanCount = kv.Value.total - kv.Value.bot,
            BotRate = kv.Value.total > 0 ? Math.Round((double)kv.Value.bot / kv.Value.total, 4) : 0,
            AvgConfidence = kv.Value.total > 0 ? Math.Round(kv.Value.confSum / kv.Value.total, 4) : 0,
            LastSeen = kv.Value.lastSeen,
            Versions = new Dictionary<string, int>(),
            Countries = new Dictionary<string, int>(),
            AvgProcessingTimeMs = 0,
        }).OrderByDescending(u => u.TotalCount).ToList();
    }

    private static string InferUaCategory(string family)
    {
        var f = family.ToLowerInvariant();
        if (f is "chrome" or "firefox" or "safari" or "edge" or "opera" or "brave" or "vivaldi" or "samsung internet")
            return "Browser";
        if (f.Contains("bot") || f.Contains("spider") || f.Contains("crawl"))
            return "Crawler";
        if (f.Contains("curl") || f.Contains("wget") || f.Contains("http") || f.Contains("python") || f.Contains("java") || f.Contains("go-http") || f.Contains("node"))
            return "Tool";
        return "Other";
    }
}
