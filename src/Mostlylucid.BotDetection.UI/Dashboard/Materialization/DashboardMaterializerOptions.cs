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
    ///     §7 Tier 1 (pinned coverage) — the page manifests kept warm every tick at every
    ///     <see cref="PrewarmWindows"/> token, regardless of live/demand status. The
    ///     operator's architecture (2026-08-11): the materializer PREWARMS the default
    ///     view of every top-level page — traffic AND the four cache-gated rows
    ///     (clusters/topbots/sessions/threats) — so the SSR reads a populated cache and
    ///     "Warming up" is never displayed. Defaults to all five seeded manifests; a host
    ///     can narrow it (e.g. single-page hosts). <see cref="PrewarmPageKey"/> remains for
    ///     back-compat as the single-key form.
    /// </summary>
    public IReadOnlyList<string> PrewarmPageKeys { get; set; } = new[]
    {
        "dashboard.traffic",
        "dashboard.topbots",
        "dashboard.clusters",
        "dashboard.sessions",
        "dashboard.threats",
        // The Site page (SiteController/SiteHealthVC) composes its own bundle under
        // this manifest — without pinned coverage the site page's first load after a
        // deploy cold-misses and the summary strip paints "0 req" until a later tick
        // (the 2026-08-12 staging finding).
        "dashboard.site",
    };

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
    // 12h included (operator directive 2026-08-14): the period-selector's standard
    // windows are 6h/12h/24h/7d/30d — the deliberate 12h exclusion predates the
    // selector and left every 12h switch cold-missing (chart spins at first paint).
    public IReadOnlyList<string> PrewarmWindows { get; set; } = new[] { "6h", "12h", "24h", "7d", "30d" };

    /// <summary>
    ///     Disk persistence for the rendered-content caches (operator directive
    ///     2026-08-11): when set, the L1 shingle cache (rendered widget HTML) is
    ///     snapshotted to this local file each materializer cycle and restored on warm
    ///     boot BEFORE the materializer refreshes — a restart with the gateway/DB down
    ///     still serves the last-known-good rendered widgets. Empty = disabled.
    /// </summary>
    public string DiskCachePath { get; set; } = "";

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

    /// <summary>
    ///     The pinned tier's wave size during the BOOT pass only (operator directive
    ///     2026-08-14: every standard window's chart must prerender at first paint, even on
    ///     a fresh boot). The pinned tier is deliberately serial in steady-state ticks (the
    ///     FOSS SQLite contention rationale — expensive 7d/30d cold reads must not contend),
    ///     but on the remote-mode host the compose is a gateway round-trip, not a local
    ///     scan — a serial 30-envelope boot pass took 30+ seconds, so the first paint after
    ///     a deploy spun for every non-default window. The boot pass runs this many pinned
    ///     composes in parallel (bounded, background — never blocks boot; BootPrewarmTimeoutMs
    ///     unchanged). Steady-state ticks keep the serial pinned tier.
    /// </summary>
    public int BootPinnedWarmConcurrency { get; set; } = 8;

    /// <summary>
    ///     The BOOT pass's wave deadline in seconds (operator gate 2026-08-14: every
    ///     standard window prerendered at first paint on a fresh boot). Steady-state
    ///     ticks cut their waves at <see cref="MaxTickDurationMs"/> (30s) — on a slow
    ///     compose path (staging evidence: 30-60s per compose under the gateway's
    ///     pressure window) the pass warms only 1-2 envelopes per tick, so the full
    ///     30-envelope set took 20+ minutes. The boot pass runs its waves against this
    ///     longer deadline (default 180s — 30 envelopes / 8-parallel × ~45s), so the
    ///     whole pinned set lands within the first pass. Background — never blocks boot
    ///     (BootPrewarmTimeoutMs unchanged); the L2 gate budget is untouched.
    /// </summary>
    public int BootPrewarmMaxDurationSeconds { get; set; } = 180;

    /// <summary>
    ///     The due-gate backoff for a FAILED warm (2026-08-14): a poison-guard Warming
    ///     result is stamped as "due again in this many seconds" instead of the full
    ///     refresh interval — the 60s due-window previously throttled the failed set to
    ///     one re-attempt per interval (the staging 14-cold-forever class: the queue sat
    ///     empty through every due-window while the failed envelopes waited). Zero
    ///     disables (failures stay un-stamped → every-tick retries). Default 5s.
    /// </summary>
    public int FailureRetryBackoffSeconds { get; set; } = 5;

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

    /// <summary>
    ///     Run one materializer pass at host start (from <c>StartAsync</c>) so the pinned
    ///     <see cref="PrewarmPageKey"/> windows are composed BEFORE the first request can
    ///     land. Without this, the very first warm waits for the first wall-clock Tick10s
    ///     boundary (up to 10s after boot), so the first request on a host whose in-request
    ///     cold-miss fallback is skipped (remote viewer mode) paints the warming shell for
    ///     that window.
    ///     <para>
    ///         Off by default: FOSS hosts cover the same gap with the request path's
    ///         synchronous first-warm fallback, and a boot-time compose pass is a behavior
    ///         change no host should inherit silently. Hosts that want a pre-rendered first
    ///         paint opt in (the commercial website does). The pass reuses
    ///         <see cref="DashboardMaterializerCoordinator.MaterializeTickAsync"/> — one
    ///         implementation of "do a materializer pass", fired early because the tick
    ///         loop's first fire is wall-clock aligned. See
    ///         <see cref="BootPrewarmTimeoutMs"/> for the boot-delay bound.
    ///     </para>
    /// </summary>
    public bool BootPrewarmEnabled { get; set; } = false;

    /// <summary>
    ///     Upper bound (ms) on how long host startup awaits the boot prewarm pass
    ///     (<see cref="BootPrewarmEnabled"/>) before declaring ready. The pass itself keeps
    ///     running in the background if it overruns — this only bounds the boot delay, never
    ///     the work (the pass is fault-observed either way, so boot can never hang or crash
    ///     on it). &lt;= 0 awaits nothing (pure fire-and-forget; the first request can still
    ///     race the pass). Default 30s, matching <see cref="MaxTickDurationMs"/>'s budget for
    ///     the same serial pinned-tier warm.
    /// </summary>
    public int BootPrewarmTimeoutMs { get; set; } = 30_000;

    /// <summary>
    ///     First-paint stash wait (operator directive 2026-08-12: pages NEVER load with empty
    ///     data). When a PINNED default view's envelope is still warming — a first load racing
    ///     the materializer's boot pass or next tick — the page controller holds the paint and
    ///     re-reads the content cache until the stash lands, bounded by this timeout. The read
    ///     itself never composes; the materializer warms on its own schedule. &lt;= 0 disables
    ///     the wait (immediate fall-through to the self-fetch paths). Custom filters (non-pinned
    ///     tokens, domain selections) never wait — they keep the sanctioned spin + SignalR fill.
    /// </summary>
    public int FirstPaintStashWaitMs { get; set; } = 2_000;

    /// <summary>Poll interval for the first-paint stash wait.</summary>
    public int FirstPaintStashPollMs { get; set; } = 100;
}
