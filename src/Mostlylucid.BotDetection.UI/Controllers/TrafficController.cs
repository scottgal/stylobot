using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Renders the Traffic landing page (GA-style overview) of the new dashboard
///     IA. URL query parameters (?country, ?bot_type, ?window, ?threat) bind into
///     <see cref="TrafficFilters"/>; the controller reads the in-process
///     <see cref="SignatureAggregateCache"/> for the visitor universe, applies the
///     filters, buckets the matching activity into a timeseries, and projects the
///     five core breakdown cards. T2 fills the chart partial and T3 fills the
///     per-card partials.
/// </summary>
[Route("dashboard/traffic")]
public sealed class TrafficController : Controller
{
    private readonly SignatureAggregateCache _cache;
    private readonly IOptions<DashboardLayoutOptions> _layout;

    public TrafficController(SignatureAggregateCache cache, IOptions<DashboardLayoutOptions> layout)
    {
        _cache = cache;
        _layout = layout;
    }

    [HttpGet("")]
    public IActionResult Index(
        [FromQuery] string? country,
        [FromQuery(Name = "bot_type")] string? botType,
        [FromQuery] string? window,
        [FromQuery] string? threat)
    {
        var opts = _layout.Value;
        var filters = new TrafficFilters(
            Country: string.IsNullOrWhiteSpace(country) ? null : country,
            BotType: string.IsNullOrWhiteSpace(botType) ? null : botType,
            Window: string.IsNullOrWhiteSpace(window) ? $"{opts.DefaultTimeWindowMinutes}m" : window,
            Threat: string.IsNullOrWhiteSpace(threat) ? null : threat);

        var topN = opts.TrafficCardTopN;
        var visitors = ResolveVisitors(filters);
        var model = new TrafficPageModel(
            Filters: filters,
            Timeseries: BuildTimeseries(visitors, filters.Window),
            Countries: TopByCountry(visitors, topN),
            BotTypes: TopByBotType(visitors, topN),
            TopEndpoints: TopByEndpoint(visitors, topN),
            TopVisitors: visitors.Take(topN).ToList(),
            Threats: visitors
                .Where(v => v.ThreatBand is "Medium" or "High" or "Critical")
                .Take(topN)
                .Select(v => new ThreatRow(
                    v.PrimarySignature,
                    v.BotName ?? string.Empty,
                    v.ThreatBand ?? "None",
                    v.LastSeen))
                .ToList());

        // Views live under the non-conventional /Views/StyloBot/Dashboard/... root
        // alongside the rest of the middleware-rendered dashboard pages, so the
        // explicit path keeps MVC's view engine off the convention-based search.
        return View("/Views/StyloBot/Dashboard/Traffic/Index.cshtml", model);
    }

    /// <summary>
    ///     Snapshot the cache and apply URL filters as a post-filter. The cache's
    ///     <see cref="SignatureAggregateCache.GetFiltered"/> accepts an audience
    ///     filter ("all" / "bots" / "humans" / "ai" / "search" / "tools") and
    ///     paging hooks but does not expose per-country / per-bot-type / per-threat
    ///     filtering, so the controller pulls the unfiltered set and narrows it
    ///     here. The LFU cache caps at 500 entries so this stays cheap.
    /// </summary>
    private IReadOnlyList<CachedVisitor> ResolveVisitors(TrafficFilters f)
    {
        // pageSize: int.MaxValue equivalent is the full snapshot — the cache caps
        // its dictionary at MaxEntries (500) so a single page reliably contains
        // everything in the hot tier.
        var (all, _, _, _) = _cache.GetFiltered(
            filter: "all", sortField: "lastSeen", sortDir: "desc",
            page: 1, pageSize: 10_000);

        IEnumerable<CachedVisitor> q = all;
        if (f.Country is { Length: > 0 } c)
            q = q.Where(v => string.Equals(v.CountryCode, c, StringComparison.OrdinalIgnoreCase));
        if (f.BotType is { Length: > 0 } bt)
            q = q.Where(v => string.Equals(v.BotType, bt, StringComparison.OrdinalIgnoreCase));
        if (f.Threat is { Length: > 0 } th)
        {
            // ?threat=Medium+ means Medium or higher; trailing + is conventional.
            var min = th.TrimEnd('+');
            q = q.Where(v => ThreatRank(v.ThreatBand) >= ThreatRank(min));
        }
        return q.OrderByDescending(v => v.LastSeen).ToList();
    }

    private static int ThreatRank(string? band) => band switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0,
    };

    /// <summary>
    ///     Bucket the visitor rows into a fixed-resolution timeseries split by
    ///     audience class derived from <see cref="CachedVisitor.BotProbability"/>
    ///     (&lt; 0.3 human, 0.3-0.8 suspicious, &gt;= 0.8 bot). The bucket count is
    ///     capped at 60 regardless of window so the chart axis stays readable.
    /// </summary>
    private static TrafficTimeseries BuildTimeseries(IReadOnlyList<CachedVisitor> rows, string window)
    {
        var minutes = ParseWindow(window);
        var now = DateTime.UtcNow;
        var bucketCount = Math.Min(60, minutes);
        var bucketSizeMin = Math.Max(1, minutes / bucketCount);
        var buckets = new DateTime[bucketCount];
        var human = new int[bucketCount];
        var susp = new int[bucketCount];
        var bot = new int[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            buckets[i] = now.AddMinutes(-minutes + i * bucketSizeMin);

        foreach (var v in rows)
        {
            var ageMin = (now - v.LastSeen).TotalMinutes;
            if (ageMin < 0 || ageMin > minutes) continue;
            var idx = bucketCount - 1 - (int)(ageMin / bucketSizeMin);
            if (idx < 0 || idx >= bucketCount) continue;
            if (v.BotProbability >= 0.8) bot[idx] += v.Hits;
            else if (v.BotProbability >= 0.3) susp[idx] += v.Hits;
            else human[idx] += v.Hits;
        }
        return new TrafficTimeseries(buckets, human, susp, bot);
    }

    private static int ParseWindow(string window) => window switch
    {
        "15m" => 15,
        "60m" or "1h" => 60,
        "24h" or "1d" => 24 * 60,
        "7d" => 7 * 24 * 60,
        _ => 60,
    };

    private static IReadOnlyList<CountryRow> TopByCountry(IReadOnlyList<CachedVisitor> rows, int topN) =>
        rows.Where(v => !string.IsNullOrEmpty(v.CountryCode))
            .GroupBy(v => v.CountryCode!)
            .Select(g => new CountryRow(
                CountryCode: g.Key,
                Hits: g.Sum(v => v.Hits),
                BotShare: g.Sum(v => v.IsBot ? v.Hits : 0) / (double)Math.Max(1, g.Sum(v => v.Hits))))
            .OrderByDescending(r => r.Hits)
            .Take(topN)
            .ToList();

    private static IReadOnlyList<BotTypeRow> TopByBotType(IReadOnlyList<CachedVisitor> rows, int topN) =>
        rows.Where(v => v.IsBot && !string.IsNullOrEmpty(v.BotType))
            .GroupBy(v => v.BotType!)
            .Select(g => new BotTypeRow(g.Key, g.Sum(v => v.Hits)))
            .OrderByDescending(r => r.Hits)
            .Take(topN)
            .ToList();

    /// <summary>
    ///     Endpoint rollup keyed on <see cref="CachedVisitor.LastPath"/>. The
    ///     CachedVisitor projection does not carry HTTP method (the cache stores
    ///     paths only), so the row label defaults to "GET" — reasonable for an
    ///     overview surface, and the per-endpoint detail page (Si tasks later)
    ///     joins to the per-request log when an exact method is needed.
    /// </summary>
    private static IReadOnlyList<EndpointRow> TopByEndpoint(IReadOnlyList<CachedVisitor> rows, int topN) =>
        rows.Where(v => !string.IsNullOrEmpty(v.LastPath))
            .GroupBy(v => v.LastPath!)
            .Select(g => new EndpointRow(
                Method: "GET",
                Path: g.Key,
                Hits: g.Sum(v => v.Hits),
                BotShare: g.Sum(v => v.IsBot ? v.Hits : 0) / (double)Math.Max(1, g.Sum(v => v.Hits))))
            .OrderByDescending(r => r.Hits)
            .Take(topN)
            .ToList();
}