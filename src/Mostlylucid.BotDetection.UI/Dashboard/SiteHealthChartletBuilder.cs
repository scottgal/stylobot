using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Projects a window of <see cref="DegradationSnapshot"/> records into two
///     <see cref="ChartletViewModel"/>s rendered side-by-side on the Traffic
///     page: upstream latency (single Line) and error-rate breakdown (StackedArea
///     of 4xx + 5xx + 429). Same <c>sb-chartlet</c> primitive that backs the
///     hits-per-period chartlet -- no new chart library, no new partial.
///     <para>
///         Per <c>project_dashboard_color_semantics</c>: 5xx + 429 use the
///         danger token; 4xx uses warning; latency uses neutral so the line
///         reads as a pure performance signal without colour-encoding a
///         verdict. Two charts side-by-side instead of a dual-axis overlay
///         because ms and percent have unrelated units; an overlaid axis
///         would mislead.
///     </para>
///     <para>
///         <see cref="ChartletSeries.Buckets"/> is <c>IReadOnlyList&lt;long&gt;</c>
///         so the error rates are scaled to integer percent (rate × 100,
///         rounded) and the formatter token <c>percent</c> renders them as
///         "12%" on the Y axis. Latency is already ms.
///     </para>
/// </summary>
public static class SiteHealthChartletBuilder
{
    /// <summary>
    ///     Build the latency chartlet (single Line series, p95 ms). When the
    ///     atom grows a distinct p50 estimator the second series lights up
    ///     automatically by extending <c>Families</c> below.
    /// </summary>
    public static ChartletViewModel BuildLatency(IReadOnlyList<DegradationSnapshot> history, string window)
    {
        var (bucketCount, bucketSpan, labelFormat) = ResolveBucketShape(window);
        var (labels, indexAt) = BuildBucketAxes(bucketCount, bucketSpan, labelFormat);

        var p95 = new long[bucketCount];
        var p95Count = new int[bucketCount];
        foreach (var s in history)
        {
            var idx = indexAt(s.TimestampUtc);
            if (idx < 0) continue;
            // Average the EWMA samples that land in the same bucket so the
            // chartlet smooths instead of showing the last-write-wins value.
            p95[idx] += (long)Math.Round(s.LatencyP95Ms);
            p95Count[idx]++;
        }
        for (var i = 0; i < bucketCount; i++)
        {
            if (p95Count[i] > 0) p95[i] /= p95Count[i];
        }

        return new ChartletViewModel(
            Id: "site-health-latency",
            Kind: ChartletKind.Line,
            BucketLabels: labels,
            Series: new List<ChartletSeries>
            {
                new(
                    Key: "p95",
                    Label: "Upstream p95",
                    ColorToken: "--sb-color-risk-unknown",
                    Buckets: p95),
            },
            Axes: new ChartletAxes(YLabel: "p95 ms", YFormat: "ms", XLabel: "time", GridLines: true),
            Drill: null);
    }

    /// <summary>
    ///     Build the error-rate chartlet (StackedArea: 4xx + 5xx + 429 as
    ///     integer-percent series). Empty bucket means the gateway saw no
    ///     traffic in that slice -- the stack still renders zero so the
    ///     time axis stays aligned with the latency chart next to it.
    /// </summary>
    public static ChartletViewModel BuildErrors(IReadOnlyList<DegradationSnapshot> history, string window)
    {
        var (bucketCount, bucketSpan, labelFormat) = ResolveBucketShape(window);
        var (labels, indexAt) = BuildBucketAxes(bucketCount, bucketSpan, labelFormat);

        var arms = new (string Key, string Label, string Token, Func<DegradationSnapshot, double> Read)[]
        {
            ("5xx", "5xx errors",    "--sb-color-risk-veryhigh", s => s.Latency5xxRate),
            ("429", "Rate limited",  "--sb-color-risk-high",     s => s.Latency429Rate),
            ("4xx", "4xx errors",    "--sb-color-risk-elevated", s => s.Latency4xxRate),
        };
        var buckets = arms.ToDictionary(a => a.Key, a => (Sum: new long[bucketCount], Count: new int[bucketCount]));

        foreach (var s in history)
        {
            var idx = indexAt(s.TimestampUtc);
            if (idx < 0) continue;
            foreach (var a in arms)
            {
                var pct = (long)Math.Round(Math.Clamp(a.Read(s), 0.0, 1.0) * 100.0);
                var b = buckets[a.Key];
                b.Sum[idx] += pct;
                b.Count[idx]++;
            }
        }

        var series = arms.Select(a =>
        {
            var (sum, count) = buckets[a.Key];
            var avg = new long[bucketCount];
            for (var i = 0; i < bucketCount; i++)
            {
                if (count[i] > 0) avg[i] = sum[i] / count[i];
            }
            return new ChartletSeries(
                Key: a.Key,
                Label: a.Label,
                ColorToken: a.Token,
                Buckets: avg);
        }).ToList();

        return new ChartletViewModel(
            Id: "site-health-errors",
            Kind: ChartletKind.StackedArea,
            BucketLabels: labels,
            Series: series,
            Axes: new ChartletAxes(YLabel: "rate", YFormat: "percent", XLabel: "time", GridLines: true),
            Drill: null);
    }

    /// <summary>
    ///     True when every snapshot in <paramref name="history"/> reports a
    ///     fully-healthy stack (no 4xx/5xx/429 signal). Callers render the
    ///     "upstream healthy — no incidents recorded in window" empty state
    ///     instead of two flat-zero charts.
    /// </summary>
    public static bool IsAllHealthy(IReadOnlyList<DegradationSnapshot> history)
    {
        if (history.Count == 0) return true;
        const double Epsilon = 0.005; // 0.5% noise floor
        foreach (var s in history)
        {
            if (s.Latency5xxRate > Epsilon) return false;
            if (s.Latency4xxRate > Epsilon) return false;
            if (s.Latency429Rate > Epsilon) return false;
            if (s.NotFoundRate > Epsilon) return false;
        }
        return true;
    }

    /// <summary>
    ///     Shared bucket shape for both the latency + error chartlets so they
    ///     line up. UX1: 6h / 12h / 24h match the hits-per-period builder's
    ///     72-bucket density so the two chartlet rows share an X axis when
    ///     they're stacked.
    /// </summary>
    private static (int BucketCount, TimeSpan BucketSpan, string LabelFormat) ResolveBucketShape(string window)
    {
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
        return (bucketCount, bucketSpan, labelFormat);
    }

    /// <summary>
    ///     Build the X-axis labels + a "timestamp → bucket index" mapper, both
    ///     anchored on the same <c>UtcNow - bucketCount × bucketSpan</c>
    ///     window so the latency + error chartlets share an identical axis.
    /// </summary>
    private static (string[] Labels, Func<DateTime, int> IndexAt) BuildBucketAxes(
        int bucketCount, TimeSpan bucketSpan, string labelFormat)
    {
        var end = DateTime.UtcNow;
        var totalSpan = TimeSpan.FromTicks(bucketSpan.Ticks * bucketCount);
        var start = end - totalSpan;
        var labels = new string[bucketCount];
        for (var i = 0; i < bucketCount; i++)
        {
            labels[i] = (start + TimeSpan.FromTicks(bucketSpan.Ticks * i)).ToString(labelFormat);
        }
        int IndexAt(DateTime when)
        {
            var offset = when - start;
            if (offset.Ticks < 0) return -1;
            var idx = (int)(offset.Ticks / bucketSpan.Ticks);
            return idx < 0 || idx >= bucketCount ? -1 : idx;
        }
        return (labels, IndexAt);
    }
}