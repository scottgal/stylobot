using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Handles the batch widget update route:
///     <c>GET {basePath}/partials/update?widgets=w1,w2&amp;w1.page=2&amp;w1.filter=bots</c>
///     <para>
///     Renders each requested widget to HTML using <see cref="RazorViewRenderer"/>,
///     injects <c>hx-swap-oob="true"</c> on the root element, caches rendered chunks
///     for 2 seconds by (widgetId, sorted params), and streams all chunks as
///     <c>text/html</c> so HTMX can swap each island in place.
///     </para>
///     <para>
///     This middleware is independent of the full dashboard (<c>/_stylobot</c>).
///     Register it with <see cref="AddStyloBotWidgets"/> and
///     <c>app.UseMiddleware&lt;SbWidgetBatchMiddleware&gt;()</c>.
///     </para>
/// </summary>
public sealed class SbWidgetBatchMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly RazorViewRenderer _razorViewRenderer;
    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly LiquidWidgetRenderer _liquidRenderer;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SbWidgetBatchMiddleware> _logger;

    private static readonly TimeSpan WidgetCacheTtl = TimeSpan.FromSeconds(2);

    public SbWidgetBatchMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        RazorViewRenderer razorViewRenderer,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        LiquidWidgetRenderer liquidRenderer,
        IMemoryCache cache,
        ILogger<SbWidgetBatchMiddleware> logger)
    {
        _next = next;
        _options = options;
        _razorViewRenderer = razorViewRenderer;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _liquidRenderer = liquidRenderer;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var basePath = _options.BasePath.TrimEnd('/');

        // Handle: POST {basePath}/partials/render — Liquid template rendering for Node SDK
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.Equals($"{basePath}/partials/render", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLiquidRenderAsync(context);
            return;
        }

        // Only handle: GET {basePath}/partials/update
        if (!context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || !path.Equals($"{basePath}/partials/update", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var widgetList = context.Request.Query["widgets"].FirstOrDefault() ?? "summary";
        var widgets = widgetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        context.Response.ContentType = "text/html; charset=utf-8";

        var sb = new StringBuilder();
        foreach (var w in widgets)
        {
            var html = await RenderWidgetWithCacheAsync(context, w);
            if (!string.IsNullOrEmpty(html))
                sb.Append(html);
        }

        await context.Response.WriteAsync(sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Cache + render
    // -------------------------------------------------------------------------

    private async Task<string> RenderWidgetWithCacheAsync(HttpContext context, string widgetId)
    {
        var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);
        var cacheKey = WidgetRenderHelpers.ComputeWidgetCacheKey(widgetId, q);

        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        var html = await RenderWidgetAsync(context, widgetId, q);
        if (!string.IsNullOrEmpty(html))
        {
            html = WidgetRenderHelpers.InjectOobAttribute(html);
            _cache.Set(cacheKey, html, WidgetCacheTtl);
        }

        return html ?? "";
    }

    private async Task<string> RenderWidgetAsync(HttpContext context, string widgetId, IQueryCollection q)
    {
        try
        {
            return widgetId switch
            {
                "summary" => await RenderSummaryAsync(context),
                "visitors" => await RenderVisitorsAsync(context, q),
                "countries" => await RenderCountriesAsync(context, q),
                "endpoints" => await RenderEndpointsAsync(context, q),
                "useragents" => await RenderUserAgentsAsync(context, q),
                "sessions" => await RenderSessionsAsync(context, q),
                "topbots" or "top-visitors" or "live-visitors" or "live-activity" => await RenderTopBotsAsync(context, q, widgetId),
                "threats" => await RenderThreatsAsync(context, q),
                _ => ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SbWidgetBatch: failed to render widget '{Widget}'", widgetId);
            return "";
        }
    }

    // -------------------------------------------------------------------------
    // Per-widget render helpers
    // -------------------------------------------------------------------------

    private async Task<string> RenderSummaryAsync(HttpContext context)
    {
        var summary = await _eventStore.GetSummaryAsync();
        var model = new SummaryStatsModel { Summary = summary, BasePath = _options.BasePath.TrimEnd('/') };

        var visitorCache = context.RequestServices.GetService<VisitorListCache>();
        if (visitorCache != null)
            PopulateSessionAnalytics(model, visitorCache);

        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSummaryStats/Default.cshtml", model, context);
    }

    private async Task<string> RenderVisitorsAsync(HttpContext context, IQueryCollection q)
    {
        var visitorCache = context.RequestServices.GetRequiredService<VisitorListCache>();
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var (items, totalCount, _, _) = visitorCache.GetFiltered(filter, sortField, sortDir, page, 24);
        var model = new VisitorListModel
        {
            Visitors = items,
            Counts = visitorCache.GetCounts(),
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = 24,
            TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbVisitorList/Default.cshtml", model, context);
    }

    private async Task<string> RenderCountriesAsync(HttpContext context, IQueryCollection q)
    {
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);

        var cached = _aggregateCache.Current.Countries;
        var data = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(100);
        var model = BuildCountriesModel(sortField, sortDir, page, 20, data);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbCountriesList/Default.cshtml", model, context);
    }

    private async Task<string> RenderEndpointsAsync(HttpContext context, IQueryCollection q)
    {
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 25);

        var cached = _aggregateCache.Current.Endpoints;
        var data = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(100);
        var model = BuildEndpointsModel(sortField, sortDir, page, pageSize, data);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderUserAgentsAsync(HttpContext context, IQueryCollection q)
    {
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "requests";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);

        // Use the aggregate cache (populated by DashboardSummaryBroadcaster).
        // IDashboardEventStore does not expose a GetUserAgentStatsAsync method, so no DB
        // fallback is available when the cache is empty (e.g. immediately after startup
        // before DashboardSummaryBroadcaster has run its first tick).
        var all = _aggregateCache.Current.UserAgents;
        var model = BuildUserAgentsModel(filter, sortField, sortDir, page, 25, all);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbUserAgentsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderSessionsAsync(HttpContext context, IQueryCollection q)
    {
        var filter = q["filter"].FirstOrDefault();
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 25);
        var model = await BuildSessionsModelAsync(context, page, pageSize, filter);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSessionsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderTopBotsAsync(HttpContext context, IQueryCollection q, string routeWidgetId = "topbots")
    {
        var sortBy = q["sort"].FirstOrDefault() ?? "default";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 10, 50);
        var filter = q["filter"].FirstOrDefault() ?? "bots";
        var widgetId = q["widgetId"].FirstOrDefault() ?? routeWidgetId;
        var model = BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, widgetId);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbTopBots/Default.cshtml", model, context);
    }

    private async Task<string> RenderThreatsAsync(HttpContext context, IQueryCollection q)
    {
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 20, 100);

        List<ThreatEntry> allThreats;
        try { allThreats = await _eventStore.GetThreatsAsync(pageSize * 10); }
        catch { allThreats = []; }

        var totalCount = allThreats.Count;
        var pagedThreats = allThreats.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var model = new ThreatsListModel
        {
            Threats = pagedThreats,
            TotalCount = totalCount,
            ActiveHoneypotSessions = pagedThreats.Count(t => t.InHoneypot),
            Page = page,
            PageSize = pageSize,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbThreatsList/Default.cshtml", model, context);
    }

    // -------------------------------------------------------------------------
    // Model builders (mirrors private methods in StyloBotDashboardMiddleware)
    // -------------------------------------------------------------------------

    private TopBotsListModel BuildTopBotsModel(int page, int pageSize, string sortBy, string sortDir, string filter = "bots", string widgetId = "topbots")
    {
        var allBots = _signatureCache.GetTopBots(page: 1, pageSize: _signatureCache.MaxEntries, sortBy: sortBy, sortDir: sortDir, filter: filter);
        var grouped = WidgetRenderHelpers.CollapseGroupableIdentities(allBots);
        var pagedBots = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = grouped.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId
        };
    }

    private EndpointsListModel BuildEndpointsModel(string sortField, string sortDir, int page, int pageSize, List<DashboardEndpointStats> all)
    {
        IEnumerable<DashboardEndpointStats> sorted = sortField.ToLowerInvariant() switch
        {
            "method" => sortDir == "asc" ? all.OrderBy(e => e.Method) : all.OrderByDescending(e => e.Method),
            "path" => sortDir == "asc" ? all.OrderBy(e => e.Path) : all.OrderByDescending(e => e.Path),
            "bots" => sortDir == "asc" ? all.OrderBy(e => e.BotCount) : all.OrderByDescending(e => e.BotCount),
            "botrate" => sortDir == "asc" ? all.OrderBy(e => e.BotRate) : all.OrderByDescending(e => e.BotRate),
            "latency" => sortDir == "asc" ? all.OrderBy(e => e.AvgProcessingTimeMs) : all.OrderByDescending(e => e.AvgProcessingTimeMs),
            "threat" => sortDir == "asc" ? all.OrderBy(e => e.AvgThreatScore) : all.OrderByDescending(e => e.AvgThreatScore),
            "unique" => sortDir == "asc" ? all.OrderBy(e => e.UniqueSignatures) : all.OrderByDescending(e => e.UniqueSignatures),
            "lastseen" => sortDir == "asc" ? all.OrderBy(e => e.LastSeen) : all.OrderByDescending(e => e.LastSeen),
            _ => sortDir == "asc" ? all.OrderBy(e => e.TotalCount) : all.OrderByDescending(e => e.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new EndpointsListModel
        {
            Endpoints = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        };
    }

    private UserAgentsListModel BuildUserAgentsModel(string filter, string sortField, string sortDir, int page, int pageSize, List<DashboardUserAgentSummary> all)
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
            "category" => sortDir == "asc" ? filteredList.OrderBy(u => u.Category) : filteredList.OrderByDescending(u => u.Category),
            "botrate" => sortDir == "asc" ? filteredList.OrderBy(u => u.BotRate) : filteredList.OrderByDescending(u => u.BotRate),
            "confidence" => sortDir == "asc" ? filteredList.OrderBy(u => u.AvgConfidence) : filteredList.OrderByDescending(u => u.AvgConfidence),
            "lastseen" => sortDir == "asc" ? filteredList.OrderBy(u => u.LastSeen) : filteredList.OrderByDescending(u => u.LastSeen),
            _ => sortDir == "asc" ? filteredList.OrderBy(u => u.TotalCount) : filteredList.OrderByDescending(u => u.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new UserAgentsListModel
        {
            UserAgents = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = filteredList.Count
        };
    }

    private CountriesListModel BuildCountriesModel(string sortField, string sortDir, int page, int pageSize, List<DashboardCountryStats> all)
    {
        IEnumerable<DashboardCountryStats> sorted = sortField.ToLowerInvariant() switch
        {
            "country" => sortDir == "asc" ? all.OrderBy(c => c.CountryCode) : all.OrderByDescending(c => c.CountryCode),
            "botrate" => sortDir == "asc" ? all.OrderBy(c => c.BotRate) : all.OrderByDescending(c => c.BotRate),
            "bots" => sortDir == "asc" ? all.OrderBy(c => c.BotCount) : all.OrderByDescending(c => c.BotCount),
            "humans" => sortDir == "asc" ? all.OrderBy(c => c.HumanCount) : all.OrderByDescending(c => c.HumanCount),
            _ => sortDir == "asc" ? all.OrderBy(c => c.TotalCount) : all.OrderByDescending(c => c.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new CountriesListModel
        {
            Countries = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        };
    }

    private async Task<SessionsListModel> BuildSessionsModelAsync(HttpContext context, int page, int pageSize, string? filter)
    {
        var sessionStore = context.RequestServices.GetService<ISessionStore>();
        if (sessionStore == null)
        {
            return new SessionsListModel
            {
                Sessions = [],
                BasePath = _options.BasePath.TrimEnd('/'),
                Filter = filter
            };
        }

        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };
        const int maxFetch = 500;
        var fetchCount = Math.Min(page * pageSize + pageSize, maxFetch);
        var sessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot);
        var totalCount = sessions.Count < maxFetch ? sessions.Count : maxFetch;

        var sigLookup = await _eventStore.LoadSignatureLookupAsync();

        var entries = sessions
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionListEntry
        {
            Id = s.Id,
            Signature = s.Signature,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            RequestCount = s.RequestCount,
            DominantState = s.DominantState,
            IsBot = s.IsBot,
            AvgBotProbability = s.AvgBotProbability,
            RiskBand = s.RiskBand,
            Action = s.Action,
            BotName = sigLookup.ResolveBotName(_signatureCache, s.Signature, s.BotName),
            CountryCode = s.CountryCode,
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null
        }).ToList();

        return new SessionsListModel
        {
            Sessions = entries,
            BasePath = _options.BasePath.TrimEnd('/'),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Filter = filter
        };
    }

    // -------------------------------------------------------------------------
    // POST /partials/render — Liquid widget rendering for Node SDK
    // -------------------------------------------------------------------------

    private async Task HandleLiquidRenderAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        if (context.Request.ContentLength > 64_000)
        {
            context.Response.StatusCode = 413;
            return;
        }

        Dictionary<string, string>? body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            if (!doc.RootElement.TryGetProperty("widgets", out var widgetsEl))
            {
                context.Response.StatusCode = 400;
                return;
            }
            body = widgetsEl.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }
        catch
        {
            context.Response.StatusCode = 400;
            return;
        }

        var sb = new StringBuilder();
        foreach (var (widgetId, template) in body)
        {
            var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);
            string html = string.IsNullOrWhiteSpace(template)
                ? await RenderWidgetAsync(context, widgetId, q)
                : await RenderLiquidWidgetAsync(context, widgetId, template) ?? "";

            if (!string.IsNullOrEmpty(html))
            {
                html = WidgetRenderHelpers.InjectOobAttribute(html);
                sb.Append(html);
            }
        }

        await context.Response.WriteAsync(sb.ToString());
    }

    private async Task<string?> RenderLiquidWidgetAsync(
        HttpContext context, string widgetId, string template)
    {
        try
        {
            var data = await BuildLiquidContextAsync(context, widgetId);
            return data == null ? null
                : await _liquidRenderer.RenderAsync(widgetId, template, data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SbWidgetBatch: Liquid render failed for '{Widget}'", widgetId);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> BuildLiquidContextAsync(
        HttpContext context, string widgetId)
    {
        try
        {
            return widgetId switch
            {
                "summary" => await BuildSummaryContextAsync(),
                "topbots" or "top-visitors" or "live-visitors" or "live-activity" => await BuildTopBotsContextAsync(),
                "visitors" => BuildVisitorsContext(context),
                "countries" => await BuildCountriesContextAsync(),
                "endpoints" => await BuildEndpointsContextAsync(),
                "useragents" => BuildUserAgentsContext(),
                "threats" => await BuildThreatsContextAsync(),
                _ => new Dictionary<string, object>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SbWidgetBatch: BuildLiquidContext failed for '{Widget}'", widgetId);
            return null;
        }
    }

    private async Task<Dictionary<string, object>> BuildSummaryContextAsync()
    {
        var summary = await _eventStore.GetSummaryAsync();
        return new Dictionary<string, object>
        {
            ["bot_requests"] = summary.BotRequests,
            ["human_requests"] = summary.HumanRequests,
            ["total_requests"] = summary.TotalRequests,
            ["uncertain_requests"] = summary.UncertainRequests,
            ["bot_rate"] = summary.TotalRequests > 0
                ? (double)summary.BotRequests / summary.TotalRequests : 0d,
            ["bot_percentage"] = summary.BotPercentage,
            ["unique_signatures"] = summary.UniqueSignatures,
            ["bot_fingerprints"] = summary.BotFingerprints,
            ["human_fingerprints"] = summary.HumanFingerprints,
            ["high_risk_fingerprints"] = summary.HighRiskFingerprints,
            ["avg_processing_ms"] = summary.AverageProcessingTimeMs,
        };
    }

    private async Task<Dictionary<string, object>> BuildTopBotsContextAsync()
    {
        var allBots = _signatureCache.GetTopBots(
            page: 1, pageSize: 50, sortBy: "default", sortDir: "desc", filter: "bots");

        var bots = allBots.Select(b => new Dictionary<string, object?>
        {
            ["signature_id"] = b.PrimarySignature,
            ["bot_name"] = b.BotName,
            ["bot_type"] = b.BotType,
            ["risk_band"] = b.RiskBand,
            ["hit_count"] = b.HitCount,
            ["bot_probability"] = b.BotProbability,
            ["action"] = b.Action,
            ["country_code"] = b.CountryCode,
            ["last_seen"] = b.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["bots"] = bots };
    }

    private Dictionary<string, object> BuildVisitorsContext(HttpContext context)
    {
        var visitorCache = context.RequestServices.GetService<VisitorListCache>();
        if (visitorCache == null)
            return new Dictionary<string, object> { ["visitors"] = new List<object>() };

        var (items, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, 50);
        var visitors = items.Select(v => new Dictionary<string, object?>
        {
            ["signature_id"] = v.PrimarySignature,
            ["is_bot"] = v.IsBot,
            ["risk_band"] = v.RiskBand,
            ["bot_name"] = v.BotName,
            ["bot_type"] = v.BotType,
            ["hits"] = v.Hits,
            ["country_code"] = v.CountryCode,
            ["last_seen"] = v.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object>
        {
            ["visitors"] = visitors,
            ["total_count"] = totalCount,
        };
    }

    private async Task<Dictionary<string, object>> BuildCountriesContextAsync()
    {
        var cached = _aggregateCache.Current.Countries;
        var data = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(50);

        var countries = data.Select(c => new Dictionary<string, object?>
        {
            ["country_code"] = c.CountryCode,
            ["country_name"] = c.CountryName,
            ["total_count"] = c.TotalCount,
            ["bot_count"] = c.BotCount,
            ["human_count"] = c.HumanCount,
            ["bot_rate"] = c.BotRate,
        }).ToList();

        return new Dictionary<string, object> { ["countries"] = countries };
    }

    private async Task<Dictionary<string, object>> BuildEndpointsContextAsync()
    {
        var cached = _aggregateCache.Current.Endpoints;
        var data = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(50);

        var endpoints = data.Select(e => new Dictionary<string, object?>
        {
            ["method"] = e.Method,
            ["path"] = e.Path,
            ["total_count"] = e.TotalCount,
            ["bot_count"] = e.BotCount,
            ["human_count"] = e.HumanCount,
            ["bot_rate"] = e.BotRate,
            ["unique_signatures"] = e.UniqueSignatures,
            ["avg_processing_ms"] = e.AvgProcessingTimeMs,
            ["avg_threat_score"] = e.AvgThreatScore,
            ["last_seen"] = e.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["endpoints"] = endpoints };
    }

    private Dictionary<string, object> BuildUserAgentsContext()
    {
        var all = _aggregateCache.Current.UserAgents;
        var useragents = all.Select(u => new Dictionary<string, object?>
        {
            ["family"] = u.Family,
            ["category"] = u.Category,
            ["total_count"] = u.TotalCount,
            ["bot_count"] = u.BotCount,
            ["human_count"] = u.HumanCount,
            ["bot_rate"] = u.BotRate,
            ["avg_confidence"] = u.AvgConfidence,
            ["last_seen"] = u.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["useragents"] = useragents };
    }

    private async Task<Dictionary<string, object>> BuildThreatsContextAsync()
    {
        List<ThreatEntry> allThreats;
        try { allThreats = await _eventStore.GetThreatsAsync(50); }
        catch { allThreats = []; }

        var threats = allThreats.Select(t => new Dictionary<string, object?>
        {
            ["signature"] = t.Signature,
            ["path"] = t.Path,
            ["cve_id"] = t.CveId,
            ["cve_severity"] = t.CveSeverity,
            ["threat_score"] = t.ThreatScore,
            ["threat_band"] = t.ThreatBand,
            ["bot_name"] = t.BotName,
            ["bot_type"] = t.BotType,
            ["bot_probability"] = t.BotProbability,
            ["country_code"] = t.CountryCode,
            ["in_honeypot"] = t.InHoneypot,
            ["timestamp"] = t.Timestamp.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["threats"] = threats };
    }

    private static void PopulateSessionAnalytics(SummaryStatsModel model, VisitorListCache visitorCache)
    {
        // maxCachedVisitors must be >= VisitorListCache._maxVisitors (default 100) to get full snapshot.
        const int maxCachedVisitors = 1_000;
        var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, maxCachedVisitors);

        // Single pass to collect all counters — avoids 6 separate LINQ iterations over the same list.
        var activeThreshold = DateTime.UtcNow.AddMinutes(-5);
        int bots = 0, humans = 0, active = 0;
        int botSingles = 0, humanSingles = 0, allSingles = 0, allWithHits = 0;
        double botDurSum = 0, humanDurSum = 0, allDurSum = 0;
        int botDurCount = 0, humanDurCount = 0, allDurCount = 0;

        foreach (var v in allVisitors)
        {
            if (v.IsBot) bots++; else humans++;
            if (v.LastSeen > activeThreshold) active++;
            if (v.Hits > 0) allWithHits++;
            if (v.Hits == 1) { allSingles++; if (v.IsBot) botSingles++; else humanSingles++; }
            if (v.Hits > 1)
            {
                var dur = (v.LastSeen - v.FirstSeen).TotalSeconds;
                allDurSum += dur; allDurCount++;
                if (v.IsBot) { botDurSum += dur; botDurCount++; }
                else          { humanDurSum += dur; humanDurCount++; }
            }
        }

        model.UniqueVisitors  = totalCount;
        model.ActiveSessions  = active;
        model.BotSessions     = bots;
        model.HumanSessions   = humans;
        model.BounceRate      = allWithHits > 0 ? Math.Round((double)allSingles / allWithHits * 100, 1) : 0;
        model.HumanBounceRate = humans > 0 ? Math.Round((double)humanSingles / humans * 100, 1) : 0;
        model.BotBounceRate   = bots > 0 ? Math.Round((double)botSingles / bots * 100, 1) : 0;
        model.AvgSessionDurationSecs      = allDurCount > 0 ? Math.Round(allDurSum / allDurCount, 1) : 0;
        model.HumanAvgSessionDurationSecs = humanDurCount > 0 ? Math.Round(humanDurSum / humanDurCount, 1) : 0;
        model.BotAvgSessionDurationSecs   = botDurCount > 0 ? Math.Round(botDurSum / botDurCount, 1) : 0;
    }

}
