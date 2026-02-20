using Microsoft.AspNetCore.Http;

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
    ///     When true, extract basic browser, protocol, and country info from HTTP headers
    ///     for ALL detections (including human traffic). This enables browser/protocol/country
    ///     dashboard stats even when the detection pipeline doesn't write signals for humans.
    ///     No PII is stored — only browser family, major version, HTTP protocol, and country code.
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
    ///     "block", "throttle-tools"). Only bots are affected — human traffic passes through freely.
    ///     Default: "throttle-stealth"
    /// </summary>
    public string DataApiActionPolicyName { get; set; } = "throttle-stealth";
}