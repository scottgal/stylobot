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
    ///     Wall-clock budget (milliseconds) for a single tick's sequential warm loop.
    ///     <see cref="MaxPagesPerTick"/> bounds by COUNT, but a page's compose cost
    ///     isn't uniform -- if the underlying query degrades (e.g. corpus-scale slowness),
    ///     a handful of slow composes can still make one tick invocation run for minutes.
    ///     Since <c>ScheduleCoordinator</c>'s single-flight guard means the next Tick10s
    ///     is skipped for as long as this invocation is still running, an unbounded tick
    ///     effectively runs back-to-back with zero pacing between passes, continuously
    ///     occupying the store alongside any concurrent in-request cold-misses. Checked
    ///     between envelopes (not mid-compose): once elapsed exceeds this budget, the
    ///     remaining live envelopes defer to the next tick rather than warming regardless
    ///     of cost. Default 8000ms -- comfortably under the 10s cadence so a tick that
    ///     hits budget still leaves the coordinator idle before the next one fires.
    /// </summary>
    public int MaxTickDurationMs { get; set; } = 8000;

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
    ///     §7 Tier 1 (pinned coverage): the window tokens kept warm every tick for
    ///     <see cref="PrewarmPageKey"/>, regardless of live/demand status. Defaults to the
    ///     FOSS Traffic UI's own window-switcher buttons, so a visit at ANY of them after
    ///     an idle gap reads warm instead of paying a synchronous in-request compose —
    ///     the single-window prewarm only covered the one default window a plain,
    ///     no-query-string visit resolves to. Each token is resolved via
    ///     <c>DashboardRoutingHelpers.WindowTokenToMinutes</c> (minutes) and
    ///     <c>HitsPerPeriodChartletBuilder.BucketSizeForWindow</c> (bucket size) — the SAME
    ///     helpers a real request's window uses, so a pinned envelope's key always matches
    ///     what a real request looks up.
    /// </summary>
    public IReadOnlyList<string> PrewarmWindows { get; set; } = new[] { "6h", "24h", "7d", "30d" };

    /// <summary>
    ///     §7 Tier 3 (bounded parallelism): live envelopes are warmed in waves of at most
    ///     this many concurrent composes, mirroring <c>ScheduleCoordinator</c>'s own
    ///     <c>MaxConcurrentSubscribersPerTick</c> bounded-parallelism pattern. Kept
    ///     deliberately conservative by default — the store (SQLite FOSS / Postgres
    ///     commercial) is the shared resource a burst of concurrent composes would put
    ///     pressure on, which is exactly the failure mode the compose-batch-overload
    ///     incident this tuning follows from was about. <see cref="MaxTickDurationMs"/> is
    ///     checked BETWEEN waves (not between every item within a wave).
    /// </summary>
    public int MaxConcurrentWarmsPerTick { get; set; } = 4;
}
