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

    /// <summary>Max live envelopes the materializer will warm in a single tick (backpressure budget).</summary>
    public int MaxPagesPerTick { get; set; } = 64;

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
    public int MaxTickDurationMs { get; set; } = 30_000;

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

    // -------------------------------------------------------------------------------
    // Stage 2b: per-page-key refresh cadence. Before this, the tick loop re-warmed
    // EVERY live envelope on EVERY Tick10s (gated only by LFU-hotness ordering and
    // MaxConcurrentWarmsPerTick/MaxPagesPerTick for bounded concurrency/count) -- there
    // was no per-key notion of "this doesn't need refreshing yet." See
    // DashboardRefreshCadence.ComputeEffectiveIntervalSeconds (the pure function these
    // knobs feed) and DashboardRowFreshness (the row/widget-key -> class map).
    // -------------------------------------------------------------------------------

    /// <summary>
    ///     Hard floor: no page key ever refreshes faster than this interval, regardless of
    ///     freshness class or LFU hotness. This is a ceiling on refresh RATE (a floor on the
    ///     interval), protecting the underlying store (SQLite FOSS / Postgres commercial)
    ///     from being hammered even by the hottest, most Live-class-heavy page key. Applied
    ///     as the final clamp in <see cref="DashboardRefreshCadence.ComputeEffectiveIntervalSeconds"/>
    ///     -- nothing upstream of it (class base, hotness scaling, adaptive stretch) can push
    ///     the effective interval below this value. Default 60s.
    /// </summary>
    public int GlobalMinIntervalSeconds { get; set; } = 60;

    /// <summary>
    ///     Base refresh interval (seconds) for a page key whose bundle touches ONLY
    ///     Aggregate-class rows (Summary, Countries, Endpoints, UserAgents, Clusters --
    ///     hour-stale is explicitly tolerated for these). Default 300s (5 min).
    ///     <para>
    ///         This is a BASE, not a guarantee: LFU-hotness scaling can pull the effective
    ///         interval for a hot key down toward <see cref="GlobalMinIntervalSeconds"/>, and
    ///         the adaptive controller can stretch it upward under measured-cost pressure.
    ///         More importantly, THE NAMED INVARIANT means this value is only ever used when
    ///         a page key's bundle touches NO Live-class row -- a page key that touches even
    ///         one Live-class widget key alongside any number of Aggregate ones must use
    ///         <see cref="LiveBaseIntervalSeconds"/> instead (the MIN, never the average or the
    ///         slower value). See <see cref="DashboardRowFreshness.ClassesTouchedBy"/>.
    ///     </para>
    /// </summary>
    public int AggregateBaseIntervalSeconds { get; set; } = 300;

    /// <summary>
    ///     Base refresh interval (seconds) for a page key whose bundle touches ANY
    ///     Live/sensitive-class row (Visitors, TopBots, Sessions, Threats -- detection
    ///     output: fingerprints/names/scores, needing seconds-to-minutes freshness).
    ///     Default 60s.
    ///     <para>
    ///         THE NAMED INVARIANT ("freshness floor / min-cadence collapse"): a shared cache
    ///         entry must never serve staler than any freshness class it currently satisfies.
    ///         <c>dashboard.traffic</c> bundles Summary/Countries/Endpoints (Aggregate)
    ///         alongside the Live-class Visitors field in ONE cache entry, so its effective
    ///         cadence MUST be this value, never <see cref="AggregateBaseIntervalSeconds"/> --
    ///         over-serving the Aggregate fields is fine and expected; under-serving Visitors
    ///         is the one thing that must never happen. Computed generically per page key from
    ///         "which freshness classes does this key's widget-key bundle touch" (see
    ///         <see cref="DashboardRowFreshness"/>), not hardcoded per page key, so a future
    ///         6th page key that also mixes classes gets this for free.
    ///     </para>
    /// </summary>
    public int LiveBaseIntervalSeconds { get; set; } = 60;

    /// <summary>
    ///     PLACEHOLDER pending a real baseline from the team's load-testing agent -- NOT a
    ///     final tuned value. Wall-clock budget (milliseconds) for one tick's TOTAL measured
    ///     warm-work cost: the sum of actual time spent composing/warming every page key
    ///     warmed that tick (see <see cref="DashboardMaterializerAdaptiveController"/>), not
    ///     tick-to-tick elapsed time (which would also count idle gaps between ticks and so
    ///     could never reliably signal load).
    ///     <para>
    ///         When the smoothed measured cost trends at or above this budget, every page
    ///         key's effective refresh interval is stretched uniformly (never per-key) so
    ///         future ticks structurally do proportionally less work -- converging toward
    ///         re-serving the same cached bundle repeatedly under sustained load, rather than
    ///         degrading query latency further the way naive per-request computation would.
    ///     </para>
    ///     <para>
    ///         Chosen as half of the existing <see cref="MaxTickDurationMs"/> hard ceiling
    ///         (8000ms default): comfortably under it so the adaptive throttle starts easing
    ///         pressure well before a tick is at risk of ever hitting the hard per-tick
    ///         deadline (which only defers work to the next tick; this instead reduces how
    ///         OFTEN work is attempted in the first place). Revisit once real production
    ///         tick-cost measurements exist -- 4000ms is an engineering estimate, not a
    ///         measured SLO.
    ///     </para>
    /// </summary>
    public int RefreshCostBudgetMs { get; set; } = 4000;

    /// <summary>
    ///     EMA smoothing weight (0..1) applied to each new tick-cost sample by
    ///     <see cref="DashboardMaterializerAdaptiveController"/>. Higher values react faster
    ///     to the latest tick's cost; lower values react more slowly but smooth out
    ///     single-tick spikes. Default 0.3: a single unusually slow (or fast) tick shouldn't
    ///     alone trip (or release) the adaptive throttle, but a sustained trend in either
    ///     direction is still reflected within a handful of ticks (roughly 30-60s at
    ///     Tick10s cadence).
    /// </summary>
    public double AdaptiveCostSmoothingAlpha { get; set; } = 0.3;
}
