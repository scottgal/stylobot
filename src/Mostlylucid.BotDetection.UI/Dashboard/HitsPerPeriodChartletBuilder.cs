using System.Globalization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Projects the same <see cref="ProjectedVisitor"/> rows that feed the Traffic
///     page's side panels into a <see cref="ChartletViewModel"/> ready for the
///     shared <c>SbChartlet</c> view component. One series per known bot family
///     (plus Human / Suspicious / Unknown bucket), stacked on the period
///     selector's bucket axis, with click-to-drill wired to the controller's
///     bot_type filter so the bar click swaps just the five side panels via
///     HTMX (the drill target is <c>#traffic-panels</c>).
///     <para>
///         UX1: the period selector is <c>6h</c> / <c>12h</c> / <c>24h</c> --
///         the earlier <c>15m</c> / <c>1h</c> options were too short to show
///         meaningful traffic shape. The Y-axis is logarithmic so a 96%-bot
///         column doesn't crush the 4% human slice into one pixel
///         (Chart.js v4 stacked + logarithmic combine correctly; we floor at
///         <c>min: 1</c> in the JS to keep empty buckets at the baseline).
///     </para>
///     <para>
///         UX2: the Internal series is <see cref="ChartletSeries.Hidden"/> by
///         default. StyloBot's own internal probes dominate by volume because
///         the gateway monitors itself; default-hidden lets the operator see
///         actual customer traffic on first paint, then click the legend pill
///         to opt back into Internal.
///     </para>
///     <para>
///         No new data path: the rows come from the existing
///         event-store-driven projection in <c>TrafficController.Index</c>.
///         Each visitor's full hit count lands in its
///         <see cref="ProjectedVisitor.LastSeen"/> bucket -- the same
///         single-bucket limitation the SVG timeseries chart already lives
///         with.
///     </para>
/// </summary>
public static class HitsPerPeriodChartletBuilder
{
    /// <summary>
    ///     Family axis. Order matters for legend rendering -- audience classes
    ///     first (Human, Suspicious), then the canonical bot families, then
    ///     Unknown / Internal as catch-alls. ColorToken values use the
    ///     dashboard's risk semantics (project_dashboard_color_semantics):
    ///     verified=success (human/aligned), elevated=warning (suspicious /
    ///     uncertain bot), veryhigh=danger (confirmed bot), unknown=neutral
    ///     (internal / unclassified).
    /// </summary>
    private static readonly (string Key, string Label, string Token)[] Families =
    {
        ("Human",        "Human",         "--sb-color-risk-verified"),
        ("Suspicious",   "Suspicious",    "--sb-color-risk-elevated"),
        ("SearchEngine", "Search engine", "--sb-color-risk-verylow"),
        ("GoodBot",      "Good bot",      "--sb-color-risk-verylow"),
        ("Scraper",      "Scraper",       "--sb-color-risk-veryhigh"),
        ("Tool",         "Tool",          "--sb-color-risk-high"),
        ("Unknown",      "Unknown bot",   "--sb-color-risk-elevated"),
        ("Internal",     "Internal",      "--sb-color-risk-unknown"),
    };

    /// <summary>
    ///     Build a stacked-bar chartlet from the visitor projection. The
    ///     <paramref name="window"/> string matches the URL filter on the
    ///     Traffic page (6h / 12h / 24h). Older tokens (15m / 1h / 60m / 7d)
    ///     are still accepted so bookmarked URLs keep rendering; bucket
    ///     density adapts so the X-axis stays readable in every case.
    /// </summary>
    public static ChartletViewModel Build(IReadOnlyList<ProjectedVisitor> rows, string window)
    {
        // UX1: 6h / 12h / 24h target a constant 72-bucket density at
        // 5-min / 10-min / 20-min granularity. Legacy tokens still work
        // so bookmarked dashboard URLs don't break.
        var bucketCount = window switch
        {
            "15m"          => 15,
            "1h" or "60m"  => 60,
            "6h"           => 72,
            "12h"          => 72,
            "24h" or "1d"  => 72,
            "7d"           => 168,
            _              => 72,
        };
        var bucketSpan = window switch
        {
            "15m"          => TimeSpan.FromMinutes(1),
            "1h" or "60m"  => TimeSpan.FromMinutes(1),
            "6h"           => TimeSpan.FromMinutes(5),
            "12h"          => TimeSpan.FromMinutes(10),
            "24h" or "1d"  => TimeSpan.FromMinutes(20),
            "7d"           => TimeSpan.FromHours(1),
            _              => TimeSpan.FromMinutes(5),
        };
        var labelFormat = window switch
        {
            "7d" => "MM-dd HH:mm",
            _    => "HH:mm",
        };

        var end = DateTime.UtcNow;
        var totalSpan = TimeSpan.FromTicks(bucketSpan.Ticks * bucketCount);
        var start = end - totalSpan;

        var labels = new string[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            labels[i] = (start + TimeSpan.FromTicks(bucketSpan.Ticks * i)).ToString(labelFormat);
        }

        // Build a stable family lookup so we always emit every series even when
        // the window has no rows -- the legend stays the same shape across
        // refreshes and the chartlet client can keep its colour assignments.
        var seriesBuckets = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, _, _) in Families)
        {
            seriesBuckets[key] = new long[bucketCount];
        }

        foreach (var v in rows)
        {
            var idx = ResolveBucketIndex(v.LastSeen, start, bucketSpan, bucketCount);
            if (idx < 0) continue;

            var key = ResolveFamilyKey(v);
            if (!seriesBuckets.TryGetValue(key, out var arr))
            {
                // Defensive fall-back -- unknown family name routes to the
                // catch-all so the visitor's hits still show up.
                arr = seriesBuckets["Unknown"];
            }
            arr[idx] += v.Hits;
        }

        var series = Families
            .Select(f => new ChartletSeries(
                Key: f.Key,
                Label: f.Label,
                ColorToken: f.Token,
                Buckets: seriesBuckets[f.Key],
                // UX2: Internal starts hidden; gateway self-probes
                // dominate by volume so default-hiding lets the
                // operator see customer traffic before opting in.
                Hidden: string.Equals(f.Key, "Internal", StringComparison.Ordinal)))
            .ToList();

        return new ChartletViewModel(
            Id: "hits-per-period",
            Kind: ChartletKind.StackedBar,
            BucketLabels: labels,
            Series: series,
            // UX1: logarithmic Y so a 96%-bot column doesn't squash the
            // 4% human slice to one pixel. JS floors min at 1 because
            // Chart.js log axis rejects 0.
            Axes: new ChartletAxes(
                YLabel: "hits", YFormat: "number", XLabel: "time",
                GridLines: true, YScale: "logarithmic"),
            Drill: new ChartletDrill(
                Url: "/dashboard/traffic",
                ParamKey: "bot_type",
                PanelTarget: "#traffic-panels"));
    }

    /// <summary>
    ///     Build the hits-per-period chartlet from the REAL durable time series
    ///     (<see cref="TrafficTimeseries"/>, populated from the event store in
    ///     <c>TrafficController</c>) rather than the visitor snapshot. Each bucket
    ///     is real traffic in a real time slot, gap-filled across the window, so
    ///     the chart survives a gateway restart and never renders "empty except
    ///     the point you just visited". Two stacked series — Human and Bot —
    ///     coloured by the dashboard risk semantics (verified=human,
    ///     veryhigh=bot). Read-only: the audience split is not a bot_type the
    ///     side panels filter on, so there is no drill.
    /// </summary>
    /// <summary>
    ///     Bucket width for the hits-per-period chart, scaled so every window
    ///     lands in the ~72-90 bucket range: fine intraday, coarser for the
    ///     multi-week views. The event store buckets on its own grain; the
    ///     controller re-aggregates the returned points into these slots (see
    ///     <see cref="BuildSeries"/>), so the rendered chart width is independent
    ///     of the store's internal bucketing. Shared by both render paths
    ///     (TrafficController + the _Traffic middleware partial) so the two never
    ///     drift (feedback_no_duplication).
    /// </summary>
    public static TimeSpan BucketSizeForWindow(string window) => window switch
    {
        "15m"          => TimeSpan.FromMinutes(1),
        "60m" or "1h"  => TimeSpan.FromMinutes(1),
        "6h"           => TimeSpan.FromMinutes(5),
        "12h"          => TimeSpan.FromMinutes(10),
        "24h" or "1d"  => TimeSpan.FromMinutes(20),
        "7d"           => TimeSpan.FromHours(2),
        "30d"          => TimeSpan.FromHours(8),
        _              => TimeSpan.FromMinutes(20),
    };

    /// <summary>
    ///     Project the durable event-store time series into the page's
    ///     <see cref="TrafficTimeseries"/>. This is REAL traffic over real time:
    ///     each <see cref="DashboardTimeSeriesPoint"/> already carries its own
    ///     bucket timestamp and per-audience counts, so we floor each point onto
    ///     a fixed <paramref name="bucketSize"/> grid and sum. The grid is
    ///     gap-filled across the whole window, so an empty period renders as a
    ///     zero bar rather than a hole — and a gateway restart no longer empties
    ///     the chart, because the numbers come from the persisted store, not an
    ///     in-memory snapshot (feedback_cache_lives_where_data_lives). The store
    ///     splits audience at the bot-probability floor (bot vs human), so the
    ///     "suspicious" band stays zero and the chart shows Human vs Bot.
    /// </summary>
    public static TrafficTimeseries BuildSeries(
        IReadOnlyList<DashboardTimeSeriesPoint> points,
        DateTime start,
        DateTime end,
        TimeSpan bucketSize)
    {
        var bucketTicks = Math.Max(bucketSize.Ticks, TimeSpan.TicksPerMinute);
        // Align the window start DOWN to a bucket boundary so bucket labels are
        // stable across refreshes and point-flooring lands on the same grid.
        var alignedStartTicks = start.Ticks - (start.Ticks % bucketTicks);
        var bucketCount = (int)Math.Ceiling((end.Ticks - alignedStartTicks) / (double)bucketTicks);
        bucketCount = Math.Clamp(bucketCount, 1, 2000);

        var buckets = new DateTime[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            buckets[i] = new DateTime(alignedStartTicks + i * bucketTicks, DateTimeKind.Utc);
        }

        var human = new int[bucketCount];
        var susp = new int[bucketCount];
        var bot = new int[bucketCount];
        foreach (var p in points)
        {
            var idx = (int)((p.Timestamp.Ticks - alignedStartTicks) / bucketTicks);
            if (idx < 0 || idx >= bucketCount) continue;
            human[idx] += p.HumanCount;
            bot[idx] += p.BotCount;
        }
        return new TrafficTimeseries(buckets, human, susp, bot);
    }

    public static ChartletViewModel BuildFromSeries(TrafficTimeseries ts, string window)
    {
        // Multi-day windows need the date in the label; intraday just the clock.
        var multiDay = window is "7d" or "30d";
        var labelFormat = multiDay ? "MM-dd HH:mm" : "HH:mm";
        var labels = ts.Buckets
            .Select(b => b.ToString(labelFormat, CultureInfo.InvariantCulture))
            .ToArray();

        var humanBuckets = ts.Human.Select(x => (long)x).ToArray();
        var botBuckets = ts.Bot.Select(x => (long)x).ToArray();

        var series = new List<ChartletSeries>
        {
            new("Human", "Human", "--sb-color-risk-verified", humanBuckets),
            new("Bot", "Bot", "--sb-color-risk-veryhigh", botBuckets),
        };

        return new ChartletViewModel(
            Id: "hits-per-period",
            Kind: ChartletKind.StackedBar,
            BucketLabels: labels,
            Series: series,
            // Linear Y axis: the earlier logarithmic scale (meant to keep a
            // bot-heavy column from squashing the human slice) instead made
            // ordinary sparse traffic look like disconnected, erratic skinny
            // spikes -- log-scale visually exaggerates small bar-to-bar swings
            // near the axis floor. A stacked bar with a normal linear axis
            // reads as an honest, readable shape; a genuinely lopsided window
            // is better served by the bar's own two-colour split than by
            // distorting the axis.
            Axes: new ChartletAxes(
                YLabel: "hits", YFormat: "number", XLabel: "time",
                GridLines: true, YScale: "linear"),
            Drill: null);
    }

    /// <summary>
    ///     Audience class for a row. Mirrors the thresholds the controller
    ///     already uses in <c>BuildTimeseries</c> / <c>BuildBotFamilies</c>:
    ///     &lt; 0.3 human, 0.3–0.8 suspicious, ≥ 0.8 bot. When the row is a
    ///     bot, we prefer its <see cref="ProjectedVisitor.BotType"/> -- but only
    ///     when it matches one of the known family keys; an unfamiliar value
    ///     routes to Unknown so the legend stays bounded.
    /// </summary>
    private static string ResolveFamilyKey(ProjectedVisitor v)
    {
        var isBot = v.IsBot || v.BotProbability >= 0.8;
        if (!isBot)
        {
            return v.BotProbability >= 0.3 ? "Suspicious" : "Human";
        }

        if (string.IsNullOrWhiteSpace(v.BotType))
        {
            return "Unknown";
        }

        foreach (var (key, _, _) in Families)
        {
            if (string.Equals(key, v.BotType, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return "Unknown";
    }

    /// <summary>
    ///     Index of the bucket covering <paramref name="when"/>. Returns -1
    ///     when the timestamp is outside the window.
    /// </summary>
    private static int ResolveBucketIndex(DateTime when, DateTime start, TimeSpan bucketSpan, int bucketCount)
    {
        var offset = when - start;
        if (offset.Ticks < 0) return -1;
        var idx = (int)(offset.Ticks / bucketSpan.Ticks);
        return idx < 0 || idx >= bucketCount ? -1 : idx;
    }
}