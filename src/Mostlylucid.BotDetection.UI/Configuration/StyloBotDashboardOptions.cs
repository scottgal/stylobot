using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.OpenApi;

namespace Mostlylucid.BotDetection.UI.Configuration;

/// <summary>
///     Configuration options for the Stylobot Dashboard.
///     <para>
///     <b>SECURITY WARNING:</b> The dashboard and its API endpoints expose detection data
///     (bot classifications, signatures, country analytics, cluster info). In production,
///     you MUST configure <see cref="RequireAuthorizationPolicy"/> or <see cref="AuthorizationFilter"/>
///     to restrict access. Without authentication, anyone can query your detection data.
///     </para>
///     <example>
///     <code>
///     // Option 1: Named authorization policy
///     services.AddStyloBotDashboard(o => o.RequireAuthorizationPolicy = "AdminOnly");
///
///     // Option 2: Custom filter
///     services.AddStyloBotDashboard(o => o.AuthorizationFilter = ctx =>
///         Task.FromResult(ctx.User.Identity?.IsAuthenticated == true));
///     </code>
///     </example>
/// </summary>
public sealed class StyloBotDashboardOptions
{
    /// <summary>
    ///     URL path where the dashboard will be accessible.
    ///     Default: "/stylobot"
    /// </summary>
    public string BasePath { get; set; } = "/stylobot";

    /// <summary>
    ///     URL used for back-navigation links (e.g. the back arrow on signature detail).
    ///     Defaults to <see cref="BasePath" />. Override to point users to a host dashboard
    ///     (e.g. "/Dashboard") while keeping the FOSS middleware mounted at its own path.
    /// </summary>
    public string? NavBasePath { get; set; }

    /// <summary>
    ///     SignalR hub path for real-time updates.
    ///     Default: "/stylobot/hub"
    /// </summary>
    public string HubPath { get; set; } = "/stylobot/hub";

    /// <summary>
    ///     When true (default), the middleware renders the full dashboard page (HTML, head, body,
    ///     navigation chrome) at the root of <see cref="BasePath" /> and at <c>{BasePath}/signature/{id}</c>.
    ///     When false, those page routes fall through to host MVC routing so the host can render the
    ///     dashboard body inside its own layout via the <c>&lt;bot-detection-dashboard /&gt;</c> and
    ///     <c>&lt;bot-detection-signature /&gt;</c> tag-helpers. API endpoints, the SignalR hub, static
    ///     assets, partials, and auth/setup routes always serve from this middleware regardless of this
    ///     flag.
    ///     <para>
    ///     Default: true (preserves single-binary FOSS behaviour). Hosts integrating the dashboard
    ///     inside their own chrome should set this to false.
    ///     </para>
    /// </summary>
    public bool RenderPage { get; set; } = true;

    /// <summary>
    ///     Max distinct widget shingles (rendered OOB elements) kept resident in the LFU
    ///     render cache (<c>DashboardWidgetShingleCache</c>). Small on purpose -- only the
    ///     widget/filter/version combinations operators actively watch stay warm; the cold
    ///     tail evicts on its own. Raise it if a host drives many concurrent dashboards on
    ///     wildly different filters. Default: 256.
    /// </summary>
    public int WidgetShingleCacheMaxEntries { get; set; } = 256;

    /// <summary>
    ///     Absolute safety TTL for a widget shingle. Freshness is normally driven by the
    ///     data-change version baked into the fingerprint (a change re-keys the shingle);
    ///     this TTL only bounds how long an orphaned old-version shingle lingers before the
    ///     LFU reclaims it under no memory pressure. Default: 2 minutes.
    /// </summary>
    public TimeSpan WidgetShingleSafetyTtl { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     When true (default), the dashboard's <c>Index.cshtml</c> emits its own brand header
    ///     (logo + Dashboard pill + SignalR dot + license badge + theme picker). When false,
    ///     the host application is expected to wrap the dashboard body in its own layout / navbar
    ///     so the operator sees ONE site chrome, not two. Commercial deployments embedded inside
    ///     the marketing site set this to <c>false</c>; FOSS standalone keeps the default
    ///     because there is no surrounding chrome to defer to.
    ///     <para>
    ///     This flag controls the brand header only. The full HTML shell (DOCTYPE / head /
    ///     scripts / styles) still renders -- the dashboard is the page in that case. The
    ///     larger "host renders the dashboard body inside its own layout" path is gated by
    ///     <see cref="RenderPage" />.
    ///     </para>
    /// </summary>
    public bool RenderShell { get; set; } = true;

    /// <summary>
    ///     Whether to enable the dashboard.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Authorization policy name to require for dashboard access.
    ///     If null, an authorization filter must be configured.
    ///     Default: null (requires manual auth configuration)
    /// </summary>
    public string? RequireAuthorizationPolicy { get; set; }

    /// <summary>
    ///     Render daisyUI tooltips next to dashboard elements (stat cards, radar axes,
    ///     fingerprint profile items, endpoint columns, etc.). Tooltip text lives in
    ///     <c>Definitions/Tooltips/dashboard-tooltips.yaml</c> per the
    ///     <c>feedback_no_word_lists</c> rule; this option only controls whether the
    ///     markup is emitted at the call sites.
    ///     <para>
    ///     Default false — the FOSS dashboard stays compact for operators who already
    ///     know the surface. stylo.bot flips this to true in its
    ///     <c>appsettings.json</c> so visitors get inline glossary copy without a
    ///     separate docs page.
    ///     </para>
    /// </summary>
    public bool EnableTooltips { get; set; } = false;

    /// <summary>
    ///     Maximum number of events to keep in memory for history.
    ///     Default: 1000
    /// </summary>
    public int MaxEventsInMemory { get; set; } = 1000;

    /// <summary>
    ///     How long detections stay in the dashboard store before pruning on
    ///     startup. Default: 30 days so the dashboard can serve up-to-30-day
    ///     analytics windows (a normal analytics package). Increase for longer
    ///     history (more disk), decrease for noisier short-window dashboards.
    ///     Tests that seed historical timestamps want this larger than their
    ///     fixture's clock skew.
    /// </summary>
    public TimeSpan DetectionRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    ///     How often to broadcast summary statistics (in seconds).
    ///     Default: 5 seconds
    /// </summary>
    public int SummaryBroadcastIntervalSeconds { get; set; } = 5;

    /// <summary>
    ///     Idle-skip window for <see cref="Services.DashboardSummaryBroadcaster"/>'s
    ///     aggregate precompute. When no <c>/api/v1</c> endpoint has been hit
    ///     for longer than this many seconds, the broadcaster parks the
    ///     heavy DB fan-out until traffic resumes. Catches the
    ///     dashboard-not-open case where running the precompute every
    ///     <see cref="SummaryBroadcastIntervalSeconds"/> burns CPU and DB I/O
    ///     for a snapshot nobody reads. Set to 0 to always precompute.
    ///     Default: 120 seconds (two minutes of no consumers parks the
    ///     ticker until the next hit lands).
    /// </summary>
    public int AggregateCacheIdleSkipSeconds { get; set; } = 120;

    /// <summary>
    ///     How long after a user-driven HTMX swap (filter click, sort header, page nav)
    ///     a widget stays "user-active" -- during this window any incoming SignalR-driven
    ///     OOB refresh whose response arrives late is refused so it can't clobber the
    ///     just-applied user state. Default 3000ms. Lower values risk the race where a
    ///     refresh fired before the click but landing after the settle restores the
    ///     pre-click state.
    /// </summary>
    public int UserActiveCooldownMs { get; set; } = 3000;

    /// <summary>
    ///     Exposes the "Pin Endpoint" admin UI on the Endpoints widget.
    ///     Default <c>false</c> -- the button is a write operation
    ///     (declares a new endpoint pin / honeypot marker against the live
    ///     gateway) and must not appear on public-facing demo dashboards
    ///     where anonymous visitors can see the surface. Set <c>true</c>
    ///     only when the dashboard host gates access behind admin auth.
    ///     Gating <see cref="EnableConfigEditing"/> alone is too broad --
    ///     that flag also drives the policies tab and other read-mostly
    ///     commercial chrome, which the public demo legitimately exposes.
    /// </summary>
    public bool EnableEndpointPinning { get; set; }

    /// <summary>
    ///     Row count fetched for the unfiltered Endpoints snapshot -- the pool
    ///     SbEndpointsListViewComponent's self-fetch fallback and
    ///     StyloBotDashboardMiddleware.GetEndpointsDataAsync (which also backs the
    ///     interactive <c>/dashboard/partials/endpoints</c> htmx path) both sort/
    ///     filter/paginate in-memory from. Both call sites MUST read this single
    ///     value rather than their own literal: a store-layer SWR cache decorator
    ///     (e.g. the commercial StaleWhileRevalidatingDashboardEventStore) bakes
    ///     the count straight into its cache key, so two different counts produce
    ///     two different keys that never share a hit -- a background refresh
    ///     completing for one is invisible to a read on the other (the
    ///     "endpoints always empty" cadence bug). Default 500 matches the
    ///     superset TopN ComposeBatchAsync already uses for the other aggregate
    ///     branches (BotAggregate/Geo), large enough for in-memory sort/filter/
    ///     paginate across the whole snapshot, not just the first page.
    /// </summary>
    public int EndpointsSnapshotCount { get; set; } = 500;

    /// <summary>
    ///     Minimum interval between outbound SignalR invalidation broadcasts, in
    ///     milliseconds. At production traffic the detection pipeline fires many
    ///     events per second; without a constrainer the dashboard would receive a
    ///     beacon per event and refetch every widget on every one. This bounds
    ///     the outbound rate: invalidations within the window are coalesced into
    ///     a single batched emit when it fires. Default 10000ms (one batch every
    ///     10 seconds). Lower values are noisier; higher means a longer worst-case
    ///     lag between a real change and the dashboard seeing it.
    /// </summary>
    public int BroadcastMinIntervalMs { get; set; } = 10000;

    /// <summary>
    ///     Minimum interval between outbound world-map "attack arc" pushes, in
    ///     milliseconds. Unlike the invalidation beacons, the attack arc carries a
    ///     payload (country + risk band) and is sent to every client per bot hit,
    ///     so it can't be coalesced through the constrainer. This caps the rate so a
    ///     bot flood can't firehose every dashboard with a SignalR message per
    ///     request. The underlying detections are still all stored and counted; only
    ///     the decorative arc is sampled. Default 100ms (≤10 arcs/sec). Set to 0 to
    ///     emit an arc for every qualifying detection.
    /// </summary>
    public int AttackArcMinIntervalMs { get; set; } = 100;

    /// <summary>
    ///     Temporal-store compression knobs (detection aging / forgetting curve).
    ///     Bound from <c>StyloBot:Dashboard:TemporalStore</c>.
    ///     <para>
    ///     Detection rows keep full per-request detail while younger than
    ///     <see cref="TemporalStoreOptions.HotWindow"/>. Past it, the compression
    ///     fold progressively nulls the verbose per-request detail columns
    ///     (UA, path, justification) on LOW-importance rows — importance is
    ///     computed once at write time and stored on the row — and on ALL rows
    ///     once they pass <see cref="TemporalStoreOptions.FullAbsorptionAge"/>.
    ///     The classification/aggregate columns (counts, bot probability, risk
    ///     band, action, threat, domain) are never touched, so dashboard reads
    ///     return identical shapes with or without compression; only the
    ///     per-request drill-down detail ages out. Rows still live until
    ///     <see cref="DetectionRetention"/> deletes them.
    ///     </para>
    ///     <para>
    ///     <see cref="TemporalStoreOptions.CompressionEnabled"/> defaults to
    ///     <c>false</c>: today's raw behavior until a host opts in and validates.
    ///     </para>
    /// </summary>
    public TemporalStoreOptions TemporalStore { get; set; } = new();

    /// <summary>
    ///     Custom authorization filter (evaluated before policy).
    ///     Signature: Func&lt;HttpContext, Task&lt;bool&gt;&gt;
    ///     Return true to allow access, false to deny.
    /// </summary>
    public Func<HttpContext, Task<bool>>? AuthorizationFilter { get; set; }

    /// <summary>
    ///     When true, the dashboard is accessible without authentication.
    ///     Default: false (dashboard requires auth configuration).
    ///     Set this explicitly to allow unauthenticated access in dev/demo environments.
    ///     In production, configure AuthorizationFilter or RequireAuthorizationPolicy instead.
    /// </summary>
    public bool AllowUnauthenticatedAccess { get; set; }

    /// <summary>
    ///     Master toggle for commercial-only dashboard chrome (policies tab, advanced
    ///     investigate views, etc.). The FOSS dashboard cannot modify configuration
    ///     regardless of this flag - this is purely a read-mostly view-feature gate.
    ///     The property name is preserved for appsettings backwards compatibility;
    ///     historically it gated a save path that has since been removed.
    ///     Default: false.
    /// </summary>
    public bool EnableConfigEditing { get; set; }

    /// <summary>
    ///     When non-empty, the "Your Detection" card renders a small "Flag as
    ///     wrong" link that POSTs to this URL. Hosts use it to capture the
    ///     fingerprint id + signature + timestamp of detections the visitor
    ///     disagrees with so a debug pipeline can replay them. No-op when
    ///     unset (the link does not render). Default: null.
    /// </summary>
    public string? FeedbackFlagWrongUrl { get; set; }

    /// <summary>
    ///     When true, extract basic browser, protocol, and country info from HTTP headers
    ///     for ALL detections (including human traffic). This enables browser/protocol/country
    ///     dashboard stats even when the detection pipeline doesn't write signals for humans.
    ///     No PII is stored - only browser family, major version, HTTP protocol, and country code.
    ///     Default: false (privacy-preserving). Enable for demo/marketing dashboards.
    /// </summary>
    public bool EnrichHumanSignals { get; set; } = false;

    /// <summary>
    ///     Detection policy name registered in <c>BotDetectionOptions.Policies</c> for dashboard data API paths.
    ///     The dashboard automatically registers this policy via <c>PostConfigure&lt;BotDetectionOptions&gt;</c>
    ///     and maps all <c>{BasePath}/api/**</c> paths to it.
    ///     Default: "dashboard-api"
    /// </summary>
    public string DataApiDetectionPolicy { get; set; } = "dashboard-api";

    /// <summary>
    ///     Action policy name to execute when a bot is detected on dashboard data API endpoints.
    ///     Maps to the <c>ActionPolicyName</c> on the registered detection policy.
    ///     Uses the bot detection system's own policy registry (e.g., "throttle-stealth",
    ///     "block", "throttle-tools"). Only bots are affected - human traffic passes through freely.
    ///     Default: "throttle-stealth"
    /// </summary>
    public string DataApiActionPolicyName { get; set; } = "throttle-stealth";

    /// <summary>
    ///     Custom display names for signatures, keyed by PrimarySignature hash.
    ///     FOSS: configure in appsettings.json under <c>StyloBot:Dashboard:SignatureLabels</c>.
    ///     Commercial: edit live in the dashboard UI.
    ///     Custom names persist regardless of detection state (survive IsBot flips).
    ///     The synthesized detection name is still tracked separately and shown alongside.
    ///     Example: <c>{ "abc123...": "My Monitoring Bot" }</c>
    /// </summary>
    public Dictionary<string, string> SignatureLabels { get; set; } = new();

    /// <summary>
    ///     When true, the Tuner action surface is shown in the detection detail view.
    ///     Default: false.
    /// </summary>
    public bool EnableTuner { get; set; }

    /// <summary>
    ///     When true, enables built-in ASP.NET Core Identity bearer/cookie auth for the dashboard.
    ///     Registers <c>AddIdentityApiEndpoints&lt;StyloBotUser&gt;()</c> and mounts login/register
    ///     endpoints at <c>{BasePath}/auth/*</c>. User accounts are stored in <c>dashboard_users</c>
    ///     in the existing SQLite database. No external auth provider required.
    ///     <para>
    ///     First-run: visit <c>{BasePath}/setup</c> to create the initial admin account.
    ///     </para>
    ///     <para>
    ///     Email sender: StyloBot registers a dev no-op sender by default (logs tokens to console).
    ///     For production, register your own <c>IEmailSender&lt;StyloBotUser&gt;</c> or configure
    ///     SMTP via <c>StyloBot:Smtp</c> in appsettings.json after calling
    ///     <c>AddStyloBotSmtp()</c> on the service collection.
    ///     </para>
    ///     <para>
    ///     COMMERCIAL: Replace this with OIDC by registering your own auth scheme and setting
    ///     <c>AuthorizationFilter</c> instead. <c>RequireAuthentication</c> and OIDC are mutually exclusive.
    ///     </para>
    ///     Default: false.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>
    ///     FOSS config-credential dashboard view-auth (login page + cookie). Opt-in:
    ///     inert unless <c>Auth:Mode = Login</c> and a username + password hash are set.
    ///     Sits beside the existing auth knobs (<see cref="RequireAuthentication"/>,
    ///     <see cref="AllowUnauthenticatedAccess"/>). Commercial OIDC layers on top via
    ///     the shared <c>DashboardViewAuthDefaults.PolicyName</c> policy without forking.
    ///     Bound from <c>StyloBot:Dashboard:Auth</c>.
    /// </summary>
    public DashboardAuthOptions Auth { get; set; } = new();

    public MonitoringPackOptions MonitoringPack { get; set; } = new();

    /// <summary>
    ///     OpenAPI document(s) to load on startup. Operations are merged into the
    ///     route catalog and cross-referenced with discovered routes. Useful for
    ///     surfacing documented-but-not-implemented endpoints and (future) seeding
    ///     auto-honeypots from documented operations.
    /// </summary>
    public OpenApiSeedOptions OpenApi { get; set; } = new();

    /// <summary>
    ///     Admin control-plane endpoints (POST /admin/restart, GET|POST /admin/learning/health)
    ///     for operator setup/observability. Endpoints require a Bearer token; when
    ///     <see cref="AdminOptions.Token"/> is null/empty the routes return 404 so their
    ///     existence isn't advertised.
    /// </summary>
    public AdminOptions Admin { get; set; } = new();
}

/// <summary>
///     Temporal-store forgetting-curve knobs. One store, one row shape — aging
///     only nulls per-request detail columns by importance; there are no bucket
///     tables and no summary row type. Every knob is operator-configurable;
///     defaults are tuned for the FOSS single-host SQLite store (forgets faster
///     than a fleet-scale store would).
/// </summary>
public sealed class TemporalStoreOptions
{
    /// <summary>
    ///     Master switch for the compression fold. <c>false</c> (default) = today's
    ///     raw behavior — every row keeps full detail until retention deletes it.
    ///     Opt in per host once validated; the raw path stays forever.
    /// </summary>
    public bool CompressionEnabled { get; set; }

    /// <summary>
    ///     Rows younger than this keep full per-request resolution unconditionally.
    ///     Past it, the fold starts absorbing. Default 2h.
    /// </summary>
    public TimeSpan HotWindow { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    ///     Hard floor: past this age every row is folded (detail columns nulled)
    ///     regardless of importance — old rows are summaries of themselves.
    ///     Still under <see cref="StyloBotDashboardOptions.DetectionRetention"/>.
    ///     Default 48h.
    /// </summary>
    public TimeSpan FullAbsorptionAge { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    ///     Minimum importance weight that preserves per-request detail between
    ///     <see cref="HotWindow"/> and <see cref="FullAbsorptionAge"/>. Rows with
    ///     weight below the floor are folded as soon as they leave the hot window;
    ///     rows at/above it keep detail until full absorption. SQLite (FOSS) sets
    ///     the floor higher — compresses more eagerly. Range 0..1. Default 0.4.
    /// </summary>
    public double ImportanceFloor { get; set; } = 0.4;

    /// <summary>
    ///     Rows absorbed per fold tick (the one-at-a-time slow path). Low-importance
    ///     rows drain first. Default 200 — at the fold's 5-minute cadence that is
    ///     2400 rows/hour of steady-state compression.
    /// </summary>
    public int FoldBatchSize { get; set; } = 200;

    /// <summary>
    ///     Weight of the row's own bot probability in the write-time importance
    ///     score (0..1, blend weights sum to 1). Default 0.5.
    /// </summary>
    public double BotScoreWeight { get; set; } = 0.5;

    /// <summary>
    ///     Weight of the row's threat score in the write-time importance score.
    ///     Default 0.3.
    /// </summary>
    public double ThreatScoreWeight { get; set; } = 0.3;

    /// <summary>
    ///     Weight of the enforcement action (block/challenge/throttle/rate-limit)
    ///     in the write-time importance score — enforcement rows are the audit
    ///     trail and keep detail longest. Default 0.2.
    /// </summary>
    public double ActionWeight { get; set; } = 0.2;

    /// <summary>
    ///     Threat-score scale at which a threat score counts as full threat. The
    ///     pipeline's threat scores are already normalized 0..1, so default 1.0;
    ///     hosts with a different scale set it here.
    /// </summary>
    public double ThreatScoreNormalizer { get; set; } = 1.0;

    /// <summary>
    ///     Fusion gate: rows whose threat score is at or above this ceiling are
    ///     never fused into a summary row — threat rows keep their own row (and
    ///     detail until <see cref="FullAbsorptionAge"/>) so the threats feed and
    ///     evidence trail stay exact. Rows below the ceiling fuse like any other
    ///     low-importance row. Default 0.5.
    /// </summary>
    public double FusionThreatCeiling { get; set; } = 0.5;
}

public sealed class AdminOptions
{
    /// <summary>
    ///     Off by default. Set true in your operator-side appsettings (or via the
    ///     env var <c>StyloBot__Dashboard__Admin__Enabled=true</c>) to turn the admin
    ///     endpoints on. When false the middleware short-circuits and admin paths
    ///     fall through to the rest of the pipeline (404), so the endpoints aren't
    ///     exposed at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Shared-secret bearer token. Required when <see cref="Enabled"/> is true;
    ///     set via <c>StyloBot:Dashboard:Admin:Token</c> or the env var
    ///     <c>StyloBot__Dashboard__Admin__Token</c>. Pick something long and random;
    ///     rotated on incident. If <see cref="Enabled"/> is true but Token is empty
    ///     the endpoints return 401 with a body pointing at the missing config key --
    ///     there is no anonymous path.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    ///     Path the admin middleware listens on, relative to the host root. Default
    ///     <c>/stylobot/admin</c> -- sits under the dashboard base path so reverse-proxy
    ///     rules already in place for the dashboard cover it. Endpoints are
    ///     <c>POST {BasePath}/restart</c> and <c>GET|POST {BasePath}/learning/health</c>.
    /// </summary>
    public string BasePath { get; set; } = "/stylobot/admin";
}

public sealed class MonitoringPackOptions
{
    /// <summary>
    ///     When false (default), the monitoring pack registers no services, no
    ///     background collectors, and no dashboard tabs. Operators who want
    ///     operational metrics opt in via appsettings:
    ///     <c>StyloBot:Dashboard:MonitoringPack:Enabled = true</c> or the
    ///     Console flag <c>--enable-monitoring</c>.
    ///
    ///     Commercial variant binaries (stylobot-{variant}) flip this to true
    ///     programmatically so paid customers get monitoring out of the box.
    /// </summary>
    public bool Enabled { get; set; }
    public MonitoringMode Mode { get; set; } = MonitoringMode.Local;
    public bool IncludeAspNetHostMeters { get; set; }
    public string? GatewayMetricsUrl { get; set; }
    public TimeSpan RemotePollInterval { get; set; } = TimeSpan.FromSeconds(60);
}

public enum MonitoringMode
{
    Local,
    GatewayServer,
    RemoteClient
}