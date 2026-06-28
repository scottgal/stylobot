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
///     five core breakdown cards. The polish pass adds a top-line counter strip
///     (Total / Humans / Bots / Bot-pressure with prior-window deltas) and a
///     per-family bot sub-stack that feeds the bar-chart variant on the 24h / 7d
///     windows so the chart shape switches from "continuous flow" to "per-period
///     totals" without losing the audience split.
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
        var windowMinutes = ParseWindow(filters.Window);
        var visitors = ResolveVisitors(filters);
        var currentVisitors = FilterByWindow(visitors, windowMinutes, offsetMinutes: 0);
        var priorVisitors = FilterByWindow(visitors, windowMinutes, offsetMinutes: windowMinutes);

        var counters = BuildCounters(currentVisitors, priorVisitors);
        var timeseries = BuildTimeseries(currentVisitors, windowMinutes);
        var botFamilies = BuildBotFamilies(currentVisitors, windowMinutes);

        var model = new TrafficPageModel(
            Filters: filters,
            Counters: counters,
            Timeseries: timeseries,
            BotFamilies: botFamilies,
            Countries: TopByCountry(currentVisitors, topN),
            BotTypes: TopByBotType(currentVisitors, topN),
            TopEndpoints: TopByEndpoint(currentVisitors, topN),
            TopVisitors: currentVisitors.Take(topN).ToList(),
            Threats: currentVisitors
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
    ///     here. The LFU cache caps at 500 entries so this stays cheap. The result
    ///     is *not* yet windowed — callers split it into current vs prior windows
    ///     via <see cref="FilterByWindow"/>.
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

    /// <summary>
    ///     Time-window slice over a pre-filtered visitor set. The current window
    ///     uses offsetMinutes: 0; the prior comparable window passes the window
    ///     length as offsetMinutes so the slice covers
    ///     [now - 2*window, now - window]. The LFU cache holds only recent hot
    ///     rows so the prior window may legitimately be empty — the counter
    ///     partial renders deltas as "—" when that happens.
    /// </summary>
    private static IReadOnlyList<CachedVisitor> FilterByWindow(
        IReadOnlyList<CachedVisitor> rows, int windowMinutes, int offsetMinutes)
    {
        var now = DateTime.UtcNow;
        var upper = now.AddMinutes(-offsetMinutes);
        var lower = upper.AddMinutes(-windowMinutes);
        var hits = new List<CachedVisitor>(rows.Count);
        foreach (var v in rows)
        {
            if (v.LastSeen <= upper && v.LastSeen > lower)
                hits.Add(v);
        }
        return hits;
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
    ///     Sum current vs prior visitor sets into headline counters with signed
    ///     deltas + Inf-safe percent deltas. Bot share is the bot fraction of the
    ///     current window (zero-safe via Math.Max(1, total)).
    /// </summary>
    private static TrafficCounters BuildCounters(
        IReadOnlyList<CachedVisitor> current, IReadOnlyList<CachedVisitor> prior)
    {
        var (curTotal, curHumans, curBots) = SumHits(current);
        var (prTotal, prHumans, prBots) = SumHits(prior);
        var totalDelta = curTotal - prTotal;
        var humansDelta = curHumans - prHumans;
        var botsDelta = curBots - prBots;
        return new TrafficCounters(
            Total: curTotal,
            Humans: curHumans,
            Bots: curBots,
            BotShare: curBots / (double)Math.Max(1, curTotal),
            TotalDelta: totalDelta,
            HumansDelta: humansDelta,
            BotsDelta: botsDelta,
            TotalDeltaPct: PctDelta(curTotal, prTotal),
            HumansDeltaPct: PctDelta(curHumans, prHumans),
            BotsDeltaPct: PctDelta(curBots, prBots));
    }

    private static (int Total, int Humans, int Bots) SumHits(IReadOnlyList<CachedVisitor> rows)
    {
        var total = 0;
        var humans = 0;
        var bots = 0;
        foreach (var v in rows)
        {
            total += v.Hits;
            if (v.BotProbability >= 0.8) bots += v.Hits;
            else if (v.BotProbability < 0.3) humans += v.Hits;
            // 0.3-0.8 = suspicious: counted toward total but not human or bot.
        }
        return (total, humans, bots);
    }

    /// <summary>
    ///     Signed percent change from <paramref name="prior"/> to
    ///     <paramref name="current"/>. Inf-safe: a zero prior with a positive
    ///     current is reported as +100% (sentinel "new traffic"); both-zero is
    ///     0% so the counter strip doesn't flash NaN on a quiet console.
    /// </summary>
    private static double PctDelta(int current, int prior)
    {
        if (prior == 0) return current == 0 ? 0d : 100d;
        return (current - prior) / (double)prior * 100d;
    }

    /// <summary>
    ///     Bucket the visitor rows into a fixed-resolution timeseries split by
    ///     audience class derived from <see cref="CachedVisitor.BotProbability"/>
    ///     (&lt; 0.3 human, 0.3-0.8 suspicious, &gt;= 0.8 bot). The bucket count is
    ///     capped at 60 regardless of window so the chart axis stays readable.
    /// </summary>
    private static TrafficTimeseries BuildTimeseries(IReadOnlyList<CachedVisitor> rows, int windowMinutes)
    {
        var (buckets, bucketSizeMin, _) = BuildBucketAxis(windowMinutes);
        var bucketCount = buckets.Length;
        var human = new int[bucketCount];
        var susp = new int[bucketCount];
        var bot = new int[bucketCount];
        var now = DateTime.UtcNow;
        foreach (var v in rows)
        {
            var idx = ResolveBucketIndex(v.LastSeen, now, windowMinutes, bucketSizeMin, bucketCount);
            if (idx < 0) continue;
            if (v.BotProbability >= 0.8) bot[idx] += v.Hits;
            else if (v.BotProbability >= 0.3) susp[idx] += v.Hits;
            else human[idx] += v.Hits;
        }
        return new TrafficTimeseries(buckets, human, susp, bot);
    }

    /// <summary>
    ///     Pick the top-5 bot families by hit count over the current window and
    ///     fold everything else into a trailing "Other bots" slot. Buckets use
    ///     the same axis as <see cref="BuildTimeseries"/> so the bar partial can
    ///     align the family sub-stack with the headline bot column. Treats
    ///     null/blank BotType as "Unknown bot" so unclassified bots still show up
    ///     in the sub-stack instead of vanishing.
    /// </summary>
    private static BotFamilySeries BuildBotFamilies(IReadOnlyList<CachedVisitor> rows, int windowMinutes)
    {
        var (buckets, bucketSizeMin, _) = BuildBucketAxis(windowMinutes);
        var bucketCount = buckets.Length;
        var now = DateTime.UtcNow;

        var bots = rows.Where(v => v.BotProbability >= 0.8).ToList();
        if (bots.Count == 0)
        {
            return new BotFamilySeries(Array.Empty<string>(), Array.Empty<IReadOnlyList<int>>());
        }

        var byFamily = bots
            .GroupBy(v => string.IsNullOrWhiteSpace(v.BotType) ? "Unknown bot" : v.BotType!)
            .Select(g => (Family: g.Key, Hits: g.Sum(v => v.Hits), Members: g.ToList()))
            .OrderByDescending(g => g.Hits)
            .ToList();

        const int topN = 5;
        var top = byFamily.Take(topN).ToList();
        var rest = byFamily.Skip(topN).ToList();
        var families = top.Select(t => t.Family).ToList();
        var series = new List<IReadOnlyList<int>>(top.Count + (rest.Count > 0 ? 1 : 0));

        foreach (var (_, _, members) in top)
        {
            series.Add(BucketFamily(members, now, windowMinutes, bucketSizeMin, bucketCount));
        }
        if (rest.Count > 0)
        {
            var merged = rest.SelectMany(r => r.Members).ToList();
            families.Add("Other bots");
            series.Add(BucketFamily(merged, now, windowMinutes, bucketSizeMin, bucketCount));
        }
        return new BotFamilySeries(families, series);
    }

    private static int[] BucketFamily(
        IReadOnlyList<CachedVisitor> rows, DateTime now,
        int windowMinutes, int bucketSizeMin, int bucketCount)
    {
        var arr = new int[bucketCount];
        foreach (var v in rows)
        {
            var idx = ResolveBucketIndex(v.LastSeen, now, windowMinutes, bucketSizeMin, bucketCount);
            if (idx < 0) continue;
            arr[idx] += v.Hits;
        }
        return arr;
    }

    /// <summary>
    ///     Shared bucket-axis builder so timeseries + family series stay aligned.
    ///     For short windows (15m/60m) buckets are 1-minute wide; the 24h/7d
    ///     windows collapse to ~60 buckets each (24-min / 168-min) so the chart
    ///     stays readable. Returns the bucket-start timestamps, the per-bucket
    ///     size in minutes, and the count.
    /// </summary>
    private static (DateTime[] Buckets, int BucketSizeMin, int Count) BuildBucketAxis(int windowMinutes)
    {
        var now = DateTime.UtcNow;
        var bucketCount = Math.Min(60, windowMinutes);
        var bucketSizeMin = Math.Max(1, windowMinutes / bucketCount);
        var buckets = new DateTime[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            buckets[i] = now.AddMinutes(-windowMinutes + i * bucketSizeMin);
        return (buckets, bucketSizeMin, bucketCount);
    }

    private static int ResolveBucketIndex(
        DateTime lastSeen, DateTime now,
        int windowMinutes, int bucketSizeMin, int bucketCount)
    {
        var ageMin = (now - lastSeen).TotalMinutes;
        if (ageMin < 0 || ageMin > windowMinutes) return -1;
        var idx = bucketCount - 1 - (int)(ageMin / bucketSizeMin);
        return idx < 0 || idx >= bucketCount ? -1 : idx;
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