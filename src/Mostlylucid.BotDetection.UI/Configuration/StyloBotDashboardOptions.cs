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
}

public sealed class MonitoringPackOptions
{
    /// <summary>
    ///     When false, disables the monitoring pack and hides all pack tabs from the dashboard.
    ///     Default: true. Set to false in appsettings.json to opt out.
    /// </summary>
    public bool Enabled { get; set; } = true;
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