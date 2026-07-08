using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     The content key for the out-of-request dashboard materialization: the
///     normalized (filter + time-window) identity of a page's composed bundle.
///
///     <para>
///         Both the request path (cache-first read) and the tick-driven
///         materializer derive this from the same <see cref="DashboardPageManifest"/>
///         + <see cref="DashboardPageWindow"/>, so it MUST normalize identically on
///         both sides — a divergent key is a permanent cache miss (every request
///         recomputes). Normalization: start/end are bucketed to the window's
///         <c>BucketMinutes</c> (so sub-bucket clock drift between the read and the
///         materialize collapses to one entry); widget keys and domains are sorted
///         (order-insensitive); every dimension that changes the result set
///         (audience, prob-min, top-N, bucket) is a distinct component.
///     </para>
///
///     <para>
///         Combined with the tick, this forms the <c>SlidingCacheAtom</c> key
///         <c>(pageKey, tick, envelope)</c> per the ratified materialization shape.
///     </para>
/// </summary>
public sealed record DashboardContentEnvelope(
    string PageKey,
    string WidgetKeysCsv,
    long StartBucketTicks,
    long EndBucketTicks,
    string AudienceFilter,
    double? ProbMin,
    string DomainsCsv,
    int TopN,
    int BucketMinutes)
{
    /// <summary>
    ///     Derives the normalized envelope for a page render. Deterministic and
    ///     side-effect free: identical (manifest, window) inputs always yield an
    ///     equal envelope (record value equality).
    /// </summary>
    public static DashboardContentEnvelope From(DashboardPageManifest manifest, DashboardPageWindow window)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(window);

        var bucketMinutes = Math.Max(1, window.BucketMinutes);
        var widgetKeys = string.Join(',', manifest.WidgetKeys.OrderBy(static k => k, StringComparer.Ordinal));
        var domains = window.Domains is null || window.Domains.Count == 0
            ? string.Empty
            : string.Join(',', window.Domains.OrderBy(static d => d, StringComparer.OrdinalIgnoreCase));

        return new DashboardContentEnvelope(
            PageKey: manifest.PageKey,
            WidgetKeysCsv: widgetKeys,
            StartBucketTicks: BucketToTicks(window.StartTime, bucketMinutes),
            EndBucketTicks: BucketToTicks(window.EndTime, bucketMinutes),
            AudienceFilter: string.IsNullOrEmpty(window.AudienceFilter) ? "all" : window.AudienceFilter,
            ProbMin: window.ProbMin,
            DomainsCsv: domains,
            TopN: window.TopN,
            BucketMinutes: window.BucketMinutes);
    }

    /// <summary>Floors a timestamp to the start of its <paramref name="bucketMinutes"/> bucket (0 when null).</summary>
    private static long BucketToTicks(DateTime? time, int bucketMinutes)
    {
        if (time is null) return 0L;
        var bucketTicks = TimeSpan.TicksPerMinute * bucketMinutes;
        return time.Value.Ticks / bucketTicks * bucketTicks;
    }
}
