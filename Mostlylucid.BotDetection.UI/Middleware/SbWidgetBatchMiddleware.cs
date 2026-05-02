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
        IMemoryCache cache,
        ILogger<SbWidgetBatchMiddleware> logger)
    {
        _next = next;
        _options = options;
        _razorViewRenderer = razorViewRenderer;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _cache = cache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var basePath = _options.BasePath.TrimEnd('/');

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
                "topbots" => await RenderTopBotsAsync(context, q),
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
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
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
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

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
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

        var cached = _aggregateCache.Current.Endpoints;
        var data = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(100);
        var model = BuildEndpointsModel(sortField, sortDir, page, 25, data);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderUserAgentsAsync(HttpContext context, IQueryCollection q)
    {
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "requests";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;

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
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(q["pageSize"].FirstOrDefault(), out var ps) && ps > 0 ? ps : 25;
        var model = await BuildSessionsModelAsync(context, page, pageSize, filter);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSessionsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderTopBotsAsync(HttpContext context, IQueryCollection q)
    {
        var sortBy = q["sortBy"].FirstOrDefault() ?? "default";
        var sortDir = q["sortDir"].FirstOrDefault() ?? "desc";
        var page = int.TryParse(q["page"].FirstOrDefault(), out var p) && p > 0 ? p : 1;
        var pageSize = int.TryParse(q["pageSize"].FirstOrDefault(), out var ps) && ps > 0 ? ps : 10;
        var model = BuildTopBotsModel(page, pageSize, sortBy, sortDir);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbTopBots/Default.cshtml", model, context);
    }

    private async Task<string> RenderThreatsAsync(HttpContext context, IQueryCollection q)
    {
        var pageStr = q["page"].FirstOrDefault();
        var pageSizeStr = q["pageSize"].FirstOrDefault();
        var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
        var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Clamp(ps, 1, 100) : 20;

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
            "/Views/Shared/Components/SbThreats/Default.cshtml", model, context);
    }

    // -------------------------------------------------------------------------
    // Model builders (mirrors private methods in StyloBotDashboardMiddleware)
    // -------------------------------------------------------------------------

    private TopBotsListModel BuildTopBotsModel(int page, int pageSize, string sortBy, string sortDir)
    {
        var allBots = _signatureCache.GetTopBots(page: 1, pageSize: _signatureCache.MaxEntries, sortBy: sortBy, sortDir: sortDir);
        var pagedBots = allBots.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = allBots.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = _options.BasePath.TrimEnd('/')
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
        var sessions = await sessionStore.GetRecentSessionsAsync(pageSize, isBot);

        var entries = sessions.Select(s => new SessionListEntry
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
            BotName = s.BotName,
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
            TotalCount = entries.Count,
            Filter = filter
        };
    }

    private static void PopulateSessionAnalytics(SummaryStatsModel model, VisitorListCache visitorCache)
    {
        var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, int.MaxValue);
        var humanVisitors = allVisitors.Where(v => !v.IsBot).ToList();
        var botVisitors = allVisitors.Where(v => v.IsBot).ToList();

        model.UniqueVisitors = totalCount;
        model.ActiveSessions = allVisitors.Count(v => v.LastSeen > DateTime.UtcNow.AddMinutes(-5));
        model.BotSessions = botVisitors.Count;
        model.HumanSessions = humanVisitors.Count;

        var totalWithHits = allVisitors.Count(v => v.Hits > 0);
        model.BounceRate = totalWithHits > 0
            ? Math.Round((double)allVisitors.Count(v => v.Hits == 1) / totalWithHits * 100, 1) : 0;
        model.HumanBounceRate = humanVisitors.Count > 0
            ? Math.Round((double)humanVisitors.Count(v => v.Hits == 1) / humanVisitors.Count * 100, 1) : 0;
        model.BotBounceRate = botVisitors.Count > 0
            ? Math.Round((double)botVisitors.Count(v => v.Hits == 1) / botVisitors.Count * 100, 1) : 0;

        static double AvgDuration(IReadOnlyList<CachedVisitor> visitors)
        {
            var withDuration = visitors.Where(v => v.Hits > 1).ToList();
            if (withDuration.Count == 0) return 0;
            return Math.Round(withDuration.Average(v => (v.LastSeen - v.FirstSeen).TotalSeconds), 1);
        }

        model.AvgSessionDurationSecs = AvgDuration(allVisitors);
        model.HumanAvgSessionDurationSecs = AvgDuration(humanVisitors);
        model.BotAvgSessionDurationSecs = AvgDuration(botVisitors);
    }

}
