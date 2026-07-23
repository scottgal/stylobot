namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Tunables for the out-of-request dashboard materialization (content cache +
///     tick-driven materializer). Every knob lives here so nothing is a hard-coded
///     magic number.
/// </summary>
public sealed class DashboardMaterializerOptions
{
    /// <summary>Max distinct (envelope, tick) entries the content cache holds before importance-scored eviction.</summary>
    public int ContentCacheMaxEntries { get; set; } = 64;

    /// <summary>Sliding inactivity window before a cache entry is eligible for expiry.</summary>
    public TimeSpan ContentSlidingExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Hard ceiling on a cache entry's lifetime regardless of activity.</summary>
    public TimeSpan ContentAbsoluteExpiration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Ticks within <c>[current - RetentionRecentTicks, current]</c> are kept at
    ///     medium retention (recent history for "compare to previous tick"); older
    ///     snapshots score low and are evicted first.
    /// </summary>
    public int RetentionRecentTicks { get; set; } = 3;

    /// <summary>
    ///     When true, a request-path read for a not-yet-materialized (envelope, tick)
    ///     composes once synchronously (the cold-miss fallback) rather than returning
    ///     empty. The tick materializer normally keeps hot pages warm ahead of reads.
    /// </summary>
    public bool ComputeOnColdMiss { get; set; } = true;

    /// <summary>Max live envelopes the materializer will warm in a single tick (backpressure budget).</summary>
    public int MaxPagesPerTick { get; set; } = 32;

    /// <summary>
    ///     An envelope is "live" (kept warm by the materializer) for this many ticks
    ///     after its last read. Approximates demand-gating until SignalR presence
    ///     lands: recently-viewed envelopes stay warm; long-unviewed ones age out so
    ///     the materializer stops composing them. Default 6 ticks ≈ 60s at Tick10s.
    /// </summary>
    public int LiveEnvelopeMaxAgeTicks { get; set; } = 6;

    /// <summary>Master switch for the tick-driven materializer (the read path's cache still works when off).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Interval (milliseconds) for SignalR broadcast coalescing when the materializer emits
    ///     invalidation signals. Queued signals are batched and emitted once per window so multiple
    ///     warmed pages don't flood the hub with individual beacons. Default 500ms (2x per 10s tick).
    /// </summary>
    public int MaterializerBroadcastIntervalMs { get; set; } = 500;

    /// <summary>Country widget visualization: "bar" (default, data-focused) or "map" (geographic).</summary>
    public string CountryWidgetStyle { get; set; } = "bar";

    /// <summary>Detection shape visualization: "radar" (default, 12-axis behavioral) or "triangle" (simplified).</summary>
    public string DetectionShapeViz { get; set; } = "radar";

    /// <summary>Source breakdown: exclude internal (LAN) traffic from country/source aggregates.</summary>
    public bool SourceBreakdownExcludeInternal { get; set; } = true;

    /// <summary>Domain-filter widget: enable/disable the domain filtering panel on Traffic page.</summary>
    public bool DomainFilterEnabled { get; set; } = true;

    /// <summary>
    ///     When true, every tick unconditionally keeps <see cref="PrewarmPageKey"/> warm at the
    ///     unfiltered default window, regardless of whether any real request has read it yet or
    ///     recently (the <c>LiveEnvelopeMaxAgeTicks</c> demand-gate only re-warms pages a request
    ///     already touched). Without this, the very first visit after startup — or any visit after
    ///     an idle gap longer than the live-envelope age or the content cache's sliding expiration —
    ///     always pays a synchronous in-request compose. This is the "pre-render before requested"
    ///     half of the materializer; the demand-gated <c>LiveEnvelopes()</c> path only sustains
    ///     pages already known to be hot.
    /// </summary>
    public bool PrewarmDefaultEnvelope { get; set; } = true;

    /// <summary>The page manifest key kept warm by <see cref="PrewarmDefaultEnvelope"/>.</summary>
    public string PrewarmPageKey { get; set; } = "dashboard.traffic";

    /// <summary>
    ///     Window length (minutes) for the unconditional prewarm. Mirrors
    ///     <c>DashboardLayoutOptions.DefaultTimeWindowMinutes</c>'s default (24h) — the envelope a
    ///     plain, no-query-string visit to the Traffic page resolves to. Kept as its own setting
    ///     (rather than a cross-reference) so the materializer has no dependency on layout options;
    ///     if the site's default window changes, update both.
    /// </summary>
    public int PrewarmWindowMinutes { get; set; } = 1440;

    /// <summary>
    ///     Bucket width (minutes) for the unconditional prewarm envelope. Must match
    ///     <c>HitsPerPeriodChartletBuilder.BucketSizeForWindow</c>'s bucket for the same window
    ///     length (20 min for the 24h default) — the content envelope keys on this value, so a
    ///     mismatch prewarm a DIFFERENT cache entry than the one the real request reads.
    /// </summary>
    public int PrewarmBucketMinutes { get; set; } = 20;
}
