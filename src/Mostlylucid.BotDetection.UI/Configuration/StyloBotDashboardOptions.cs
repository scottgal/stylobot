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
    ///     Maximum number of events to keep in memory for history.
    ///     Default: 1000
    /// </summary>
    public int MaxEventsInMemory { get; set; } = 1000;

    /// <summary>
    ///     How often to broadcast summary statistics (in seconds).
    ///     Default: 5 seconds
    /// </summary>
    public int SummaryBroadcastIntervalSeconds { get; set; } = 5;

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
    ///     Authorization filter for write operations (config save/delete, policy changes).
    ///     Separate from read access - viewing the dashboard does NOT grant write permission.
    ///     If null and RequireWriteAuthorizationPolicy is also null, write operations are DENIED by default.
    /// </summary>
    public Func<HttpContext, Task<bool>>? WriteAuthorizationFilter { get; set; }

    /// <summary>
    ///     Authorization policy name required for write operations.
    ///     If null and WriteAuthorizationFilter is also null, write operations are DENIED by default.
    ///     This is separate from RequireAuthorizationPolicy (which controls read access).
    /// </summary>
    public string? RequireWriteAuthorizationPolicy { get; set; }

    /// <summary>
    ///     When true, config editing is enabled in the dashboard UI.
    ///     Even when enabled, write operations require WriteAuthorizationFilter or RequireWriteAuthorizationPolicy.
    ///     Default: false (config tab is read-only).
    /// </summary>
    public bool EnableConfigEditing { get; set; }

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
    ///     Requires a paid StyloBot license with the <c>stylobot.tuner</c> feature flag.
    ///     Set by the commercial gateway plugin via <c>AddStyloBotCommercialPlugin()</c>.
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

    public MonitoringPackOptions MonitoringPack { get; set; } = new();

    /// <summary>
    ///     OpenAPI document(s) to load on startup. Operations are merged into the
    ///     route catalog and cross-referenced with discovered routes. Useful for
    ///     surfacing documented-but-not-implemented endpoints and (future) seeding
    ///     auto-honeypots from documented operations.
    /// </summary>
    public OpenApiSeedOptions OpenApi { get; set; } = new();

    /// <summary>
    ///     Admin control-plane endpoints (POST /admin/reload, POST /admin/restart) for
    ///     applying config changes during operator setup. Both endpoints require a
    ///     Bearer token; when <see cref="AdminOptions.Token"/> is null/empty the routes
    ///     return 404 so their existence isn't advertised.
    /// </summary>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>
    ///     Tuning for the Behavioral Evolution panel on the signature-detail page.
    /// </summary>
    public BehavioralEvolutionOptions BehavioralEvolution { get; set; } = new();
}

public sealed class AdminOptions
{
    /// <summary>
    ///     Off by default. Set true in your operator-side appsettings (or via
    ///     <c>STYLOBOT_ADMIN_ENABLED=true</c>) to turn the admin endpoints on. When
    ///     false the middleware short-circuits and admin paths fall through to the
    ///     rest of the pipeline (404), so the endpoints aren't exposed at all.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Shared-secret bearer token. Required when <see cref="Enabled"/> is true;
    ///     set via <c>StyloBot:Dashboard:Admin:Token</c> or the env var
    ///     <c>STYLOBOT_ADMIN_TOKEN</c>. Pick something long and random; rotated on
    ///     incident. If <see cref="Enabled"/> is true but Token is empty the
    ///     endpoints return 401 with a body pointing at the missing config key --
    ///     there is no anonymous path.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    ///     Path the admin middleware listens on, relative to the host root. Default
    ///     <c>/stylobot/admin</c> -- sits under the dashboard base path so reverse-proxy
    ///     rules already in place for the dashboard cover it. Endpoints are
    ///     <c>POST {BasePath}/reload</c> and <c>POST {BasePath}/restart</c>.
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