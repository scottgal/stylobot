using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Renders the Traffic landing page (GA-style overview) of the new dashboard
///     IA. URL query parameters (?country, ?bot_type, ?window, ?threat) bind into
///     <see cref="TrafficFilters"/>; the controller reads
///     <see cref="IDashboardEventStore"/> as the canonical source (same pattern
///     as <c>SbVisitorListViewComponent</c> + the legacy website
///     <c>DashboardController</c>), so remote-mode hosts -- where the in-process
///     <see cref="SignatureAggregateCache"/> is empty by design -- still render
///     real numbers. The top-line counter strip comes from
///     <see cref="IDashboardEventStore.GetSummaryAsync"/>; the breakdown cards
///     come from <see cref="IDashboardEventStore.GetCountryStatsAsync"/> /
///     <see cref="IDashboardEventStore.GetEndpointStatsAsync"/> /
///     <see cref="IDashboardEventStore.GetTopBotsAsync"/> projected through
///     <see cref="WidgetRenderHelpers.ProjectAsVisitors"/>. The 24h / 7d bar
///     variant re-buckets the same data so the chart shape switches without
///     losing the audience split.
/// </summary>
[Route("dashboard/traffic")]
public sealed class TrafficController : Controller
{
    private readonly IDashboardEventStore _eventStore;
    private readonly IOptions<DashboardLayoutOptions> _layout;
    private readonly IOptions<ThreatsOptions> _threatsOpts;
    private readonly SignatureAggregateCache? _cache;

    public TrafficController(
        IDashboardEventStore eventStore,
        IOptions<DashboardLayoutOptions> layout,
        IOptions<ThreatsOptions> threatsOpts,
        SignatureAggregateCache? cache = null)
    {
        // IDashboardEventStore is the canonical, share-with-the-rest-of-the-dashboard
        // change-detection mechanism (feedback_centralised_change_detection). The
        // local SignatureAggregateCache is OPTIONAL per feedback_remote_mode_optional_di:
        // not registered on the marketing-site host. It is no longer used as a
        // data source here -- kept as a constructor dep so any future fast-path
        // can short-circuit through it without a DI rewire.
        _eventStore = eventStore;
        _layout = layout;
        _threatsOpts = threatsOpts;
        _cache = cache;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? country,
        [FromQuery(Name = "bot_type")] string? botType,
        [FromQuery] string? window,
        [FromQuery] string? threat,
        [FromQuery] int? partial,
        CancellationToken ct)
    {
        var opts = _layout.Value;
        var filters = new TrafficFilters(
            Country: string.IsNullOrWhiteSpace(country) ? null : country,
            BotType: string.IsNullOrWhiteSpace(botType) ? null : botType,
            // UX1: Window default uses the canonical period-selector
            // tokens (6h / 12h / 24h) rather than a raw "{n}m" string.
            // ParseWindow recognises both forms so legacy hosts that set
            // DefaultTimeWindowMinutes still resolve.
            Window: string.IsNullOrWhiteSpace(window) ? FormatDefaultWindow(opts.DefaultTimeWindowMinutes) : window,
            Threat: string.IsNullOrWhiteSpace(threat) ? null : threat);

        var topN = opts.TrafficCardTopN;
        var windowMinutes = ParseWindow(filters.Window);
        var now = DateTime.UtcNow;
        var startTime = now.AddMinutes(-windowMinutes);

        // 1. Top bots over the current window -- larger than topN so the
        //    visitor / family breakdowns have enough rows to group on.
        // 2. The prior comparable window for the counter delta (best-effort:
        //    the store may return an empty set on short retention windows).
        var topBotsTask = _eventStore.GetTopBotsAsync(
            count: 500, startTime: startTime, endTime: now, audienceFilter: "all");
        var priorBotsTask = _eventStore.GetTopBotsAsync(
            count: 500, startTime: startTime.AddMinutes(-windowMinutes), endTime: startTime, audienceFilter: "all");
        var summaryTask = SafeGetSummaryAsync(startTime, now);
        var priorSummaryTask = SafeGetSummaryAsync(startTime.AddMinutes(-windowMinutes), startTime);
        var countriesTask = SafeGetCountryStatsAsync(topN, startTime, now);
        var endpointsTask = SafeGetEndpointStatsAsync(topN, startTime, now);

        await Task.WhenAll(topBotsTask, priorBotsTask, summaryTask, priorSummaryTask, countriesTask, endpointsTask);

        var topBots = topBotsTask.Result;
        var priorBots = priorBotsTask.Result;
        var summary = summaryTask.Result;
        var priorSummary = priorSummaryTask.Result;
        var countriesData = countriesTask.Result;
        var endpointsData = endpointsTask.Result;

        // Project top-bots through the canonical helper so audience split,
        // collapse rules, and threat / country / bot-type post-filters match
        // the rest of the dashboard exactly.
        var (visitors, _, _) = WidgetRenderHelpers.ProjectAsVisitors(
            topBots, filter: "all", sortField: "lastSeen", sortDir: "desc",
            page: 1, pageSize: 500,
            country: filters.Country, botType: filters.BotType, threat: filters.Threat);

        var counters = BuildCounters(summary, priorSummary, topBots, priorBots);
        var timeseries = BuildTimeseries(visitors, windowMinutes);
        var botFamilies = BuildBotFamilies(visitors, windowMinutes);

        var model = new TrafficPageModel(
            Filters: filters,
            Counters: counters,
            Timeseries: timeseries,
            BotFamilies: botFamilies,
            Countries: TopByCountry(countriesData, topN),
            BotTypes: TopByBotType(visitors, topN),
            TopEndpoints: TopByEndpoint(endpointsData, topN, opts.TopEndpointsMinSamplesForPerf),
            TopVisitors: visitors.Take(topN).ToList(),
            // Delegated to ThreatsFilter so the partial (rendered by
            // _Traffic.cshtml) and this HTMX-swap controller path stay in
            // lockstep — without the shared filter, the SignalR-driven refresh
            // would silently fall back to the pre-A4 strict-band cliff and
            // empty the card on every swap (feedback_no_duplication).
            Threats: ThreatsFilter
                .Apply(visitors, _threatsOpts.Value ?? new ThreatsOptions())
                .Select(v => new ThreatRow(
                    v.PrimarySignature,
                    v.BotName ?? string.Empty,
                    v.ThreatBand ?? "None",
                    v.LastSeen))
                .ToList());

        // Views live under the non-conventional /Views/StyloBot/Dashboard/... root
        // alongside the rest of the middleware-rendered dashboard pages, so the
        // explicit path keeps MVC's view engine off the convention-based search.
        // Two narrow swap paths:
        //   ?partial=1 -- legacy SignalR-driven full _Body swap (header,
        //       counters, chart, all five panels). Used by the SignalR
        //       invalidation beacon so a single hx-trigger replaces the entire
        //       data region with one refreshed render.
        //   HX-Request -- chartlet drill swap (C1). The hits-per-period bar
        //       chartlet's Drill emits a GET back here with bot_type=<family>;
        //       sb-chartlet.js wraps that call with HX-Request: true so the
        //       controller short-circuits to just the five side panels
        //       (_TrafficPanels). Chart + counters + filter chrome stay put;
        //       only the panels swap, matching feedback_ssr_signalr_pattern's
        //       "SSR + targeted HTMX OOB" rule.
        if (partial == 1)
            return PartialView("/Views/StyloBot/Dashboard/Traffic/_Body.cshtml", model);
        if (Request.Headers.ContainsKey("HX-Request"))
            return PartialView("/Views/StyloBot/Dashboard/Traffic/_TrafficPanels.cshtml", model);
        return View("/Views/StyloBot/Dashboard/Traffic/Index.cshtml", model);
    }

    private async Task<DashboardSummary?> SafeGetSummaryAsync(DateTime start, DateTime end)
    {
        try { return await _eventStore.GetSummaryAsync(startTime: start, endTime: end); }
        catch { return null; }
    }

    private async Task<List<DashboardCountryStats>> SafeGetCountryStatsAsync(int n, DateTime start, DateTime end)
    {
        try { return await _eventStore.GetCountryStatsAsync(count: Math.Max(n, 20), startTime: start, endTime: end); }
        catch { return new List<DashboardCountryStats>(); }
    }

    private async Task<List<DashboardEndpointStats>> SafeGetEndpointStatsAsync(int n, DateTime start, DateTime end)
    {
        try { return await _eventStore.GetEndpointStatsAsync(count: Math.Max(n, 50), startTime: start, endTime: end); }
        catch { return new List<DashboardEndpointStats>(); }
    }

    /// <summary>
    ///     Counter strip: prefer the event-store's <see cref="DashboardSummary"/>
    ///     (per-detection-row sums over the time window, the same number the
    ///     dashboard header shows). Falls back to summing the visitor projection's
    ///     hit counts only if the summary call failed. Prior-window delta is the
    ///     same shape against the comparable previous window.
    /// </summary>
    private static TrafficCounters BuildCounters(
        DashboardSummary? current,
        DashboardSummary? prior,
        IReadOnlyList<DashboardTopBotEntry> currentBots,
        IReadOnlyList<DashboardTopBotEntry> priorBots)
    {
        var (curTotal, curHumans, curBots) = current is null
            ? SumHits(currentBots)
            : (current.TotalRequests, current.HumanRequests, current.BotRequests);
        var (prTotal, prHumans, prBots) = prior is null
            ? SumHits(priorBots)
            : (prior.TotalRequests, prior.HumanRequests, prior.BotRequests);

        return new TrafficCounters(
            Total: curTotal,
            Humans: curHumans,
            Bots: curBots,
            BotShare: curBots / (double)Math.Max(1, curTotal),
            TotalDelta: curTotal - prTotal,
            HumansDelta: curHumans - prHumans,
            BotsDelta: curBots - prBots,
            TotalDeltaPct: PctDelta(curTotal, prTotal),
            HumansDeltaPct: PctDelta(curHumans, prHumans),
            BotsDeltaPct: PctDelta(curBots, prBots));
    }

    private static (int Total, int Humans, int Bots) SumHits(IReadOnlyList<DashboardTopBotEntry> rows)
    {
        var total = 0; var humans = 0; var bots = 0;
        foreach (var v in rows)
        {
            total += v.HitCount;
            if (v.BotProbability >= 0.8) bots += v.HitCount;
            else if (v.BotProbability < 0.3) humans += v.HitCount;
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
    ///     Limitation: <c>CachedVisitor</c> only carries the LastSeen timestamp,
    ///     so the visitor's full hit-count lands in the single LastSeen bucket
    ///     -- a precise per-event histogram is a follow-up that needs the
    ///     event store's per-bucket time-series endpoint. The headline counters
    ///     are not affected; they sum the <see cref="DashboardSummary"/> directly.
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

    /// <summary>
    ///     Window token -> minutes. UX1: the supported tokens are
    ///     <c>6h</c> / <c>12h</c> / <c>24h</c>; the older <c>15m</c> /
    ///     <c>1h</c> / <c>7d</c> tokens still resolve so bookmarked URLs
    ///     keep working. Default is 6h to match the new selector default.
    /// </summary>
    private static int ParseWindow(string window) => window switch
    {
        "15m"           => 15,
        "60m" or "1h"   => 60,
        "6h"            => 6 * 60,
        "12h"           => 12 * 60,
        "24h" or "1d"   => 24 * 60,
        "7d"            => 7 * 24 * 60,
        _               => 6 * 60,
    };

    /// <summary>
    ///     Map <see cref="DashboardLayoutOptions.DefaultTimeWindowMinutes"/>
    ///     onto the canonical period-selector token so URLs and SQL queries
    ///     speak the same language as the pill widget on _Body.cshtml.
    /// </summary>
    private static string FormatDefaultWindow(int minutes) => minutes switch
    {
        15            => "15m",
        60            => "1h",
        360           => "6h",
        720           => "12h",
        1440          => "24h",
        7 * 24 * 60   => "7d",
        _             => "6h",
    };

    /// <summary>
    ///     Project the event-store country stats into the view's
    ///     <see cref="CountryRow"/> shape, top-N by total hits.
    /// </summary>
    private static IReadOnlyList<CountryRow> TopByCountry(IReadOnlyList<DashboardCountryStats> rows, int topN) =>
        rows.Where(r => !string.IsNullOrEmpty(r.CountryCode))
            .OrderByDescending(r => r.TotalCount)
            .Take(topN)
            .Select(r => new CountryRow(
                CountryCode: r.CountryCode,
                Hits: r.TotalCount,
                BotShare: r.TotalCount > 0 ? r.BotCount / (double)r.TotalCount : 0d))
            .ToList();

    private static IReadOnlyList<BotTypeRow> TopByBotType(IReadOnlyList<CachedVisitor> rows, int topN) =>
        rows.Where(v => v.IsBot && !string.IsNullOrEmpty(v.BotType))
            .GroupBy(v => v.BotType!)
            .Select(g => new BotTypeRow(g.Key, g.Sum(v => v.Hits)))
            .OrderByDescending(r => r.Hits)
            .Take(topN)
            .ToList();

    /// <summary>
    ///     Endpoint rollup from the event store's per-(method, path)
    ///     <see cref="DashboardEndpointStats"/> rows. Carries the real HTTP
    ///     method (R5 fix -- the old projection hardcoded "GET" because the LFU
    ///     cache dropped method).
    /// </summary>
    /// <summary>
    ///     Project the store's <see cref="DashboardEndpointStats"/> rows into the
    ///     widget's <see cref="EndpointRow"/> view-model, carrying through the
    ///     per-endpoint p95 + error rate the store already aggregates. U4: no
    ///     parallel EndpointPerfAtom needed -- the store IS the per-endpoint
    ///     truth source (and on remote-mode hosts that means the gateway's
    ///     <c>GET /api/v1/dashboard/endpoint-stats</c> via
    ///     <c>RemoteDashboardEventStore</c>). Endpoints with fewer than
    ///     <paramref name="minSamplesForPerf"/> hits get
    ///     <see cref="EndpointRow.HasPerf"/> = false so the view paints a dash
    ///     instead of misleading numbers on cold-start / brand-new paths.
    /// </summary>
    private static IReadOnlyList<EndpointRow> TopByEndpoint(
        IReadOnlyList<DashboardEndpointStats> rows, int topN, int minSamplesForPerf) =>
        rows.Where(r => !string.IsNullOrEmpty(r.Path))
            .OrderByDescending(r => r.TotalCount)
            .Take(topN)
            .Select(r =>
            {
                var hasPerf = r.TotalCount >= Math.Max(1, minSamplesForPerf);
                var errorRate = r.TotalCount > 0
                    ? (r.Status4xx + r.Status5xx) / (double)r.TotalCount
                    : 0.0;
                return new EndpointRow(
                    Method: string.IsNullOrEmpty(r.Method) ? string.Empty : r.Method,
                    Path: r.Path,
                    Hits: r.TotalCount,
                    BotShare: r.BotRate,
                    P95Ms: r.P95ProcessingTimeMs,
                    ErrorRate: errorRate,
                    HasPerf: hasPerf);
            })
            .ToList();
}